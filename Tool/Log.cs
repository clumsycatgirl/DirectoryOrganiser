using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Tool;

/// <summary>
/// Static class to handle logging functionality including asynchronous logging,
/// log rotation, log filtering, performance profiling, and context information.
/// </summary>
public static class Log {
	public static string logFilesDirectory = Path.Combine(Environment.CurrentDirectory, "Logs");

	/// <summary>
	/// Directory where log files will be stored.
	/// </summary>
	public static string LogFilesDirectory {
		get => logFilesDirectory;
		set {
			logFilesDirectory = value;
			LogFile = GetLogFileName(DateTime.Now, 0);
		}
	}

	private static string logFile = GetLogFileName(DateTime.Now, 0);

	/// <summary>
	/// The current log file being written to. When changed, it reinitializes the logger.
	/// </summary>
	public static string LogFile {
		get => logFile;
		set {
			logFile = value;
			Initialize();
		}
	}

	/// <summary>
	/// The current json log file being written to. When changed, it reinitializes the logger.
	/// </summary>
	public static string JsonLogFile => Path.ChangeExtension(LogFile, ".json");

	/// <summary>
	/// Specifies the log level for logging purposes.
	/// </summary>
	public enum LogLevel {
		None,
		Info,
		Debug,
		Warning,
		Error
	}

	/// <summary>
	/// Helper class to convert <see cref="LogLevel"/> into console colors.
	/// </summary>
	public static class LogLevelHelper {
		/// <summary>
		/// Returns the appropriate console color for a given log level.
		/// </summary>
		/// <param name="logLevel">The log level.</param>
		/// <returns>A <see cref="ConsoleColor"/> representing the color.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ConsoleColor ToConsoleColor(LogLevel logLevel) => logLevel switch {
			LogLevel.None => ConsoleColor.White,
			LogLevel.Debug => ConsoleColor.Gray,
			LogLevel.Info => ConsoleColor.Green,
			LogLevel.Warning => ConsoleColor.Yellow,
			LogLevel.Error => ConsoleColor.Red,
			_ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null)
		};
	}

	/// <summary>
	/// Boolean to determine whether logs should also be written to <seealso cref="Console.Out"/>.
	/// </summary>
	public static bool LogToConsole = false;
	/// <summary>
	/// Boolean to determine whether logs should also be written to the <seealso cref="LogFile"/>.
	/// </summary>
	public static bool LogToFile = true;
	/// <summary>
	/// Boolean to determine whether logs should also be written to the <seealso cref="JsonLogFile"/>.
	/// </summary>
	public static bool LogToJson = true;
	/// <summary>
	/// Boolean to determine whether logs should also be written to the <seealso cref="OutputStream"/>.
	/// </summary>
	public static bool LogToStream = true;

	private static TextWriter outputStream = new VSDebugWriter();

	/// <summary>
	/// The current output stream for the logs. Defaults to <see cref="Console.Out"/>.
	/// </summary>
	public static TextWriter OutputStream {
		get => outputStream;
		set => outputStream = value ?? throw new ArgumentNullException(nameof(value));
	}

	private static readonly Lazy<string> assemblyName = new(() => Assembly.GetEntryAssembly()!.GetName()!.Name!);
	private static readonly Lazy<string> assemblyVersion = new(() => Assembly.GetEntryAssembly()!.GetName()!.Version!.ToString());
	private static readonly Lazy<string> assemblyCompany = new(() => Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Unknown");

	private static StreamWriter? jsonWriter = null;
	private static StreamWriter? fileWriter = null;
	private static readonly object logLock = new();

	/// <summary>
	/// Indicates if log levels should be included in the log message.
	/// </summary>
	public static bool IncludeLogLevel = true;

	/// <summary>
	/// Indicates if timestamps should be included in the log message.
	/// </summary>
	public static bool IncludeTimestamp = true;

	/// <summary>
	/// Indicates if context information (such as method and thread ID) should be included in the log message.
	/// </summary>
	public static bool IncludeContextInformation = false;

	/// <summary>
	/// Indicates if caller method name should be included in the log message.
	/// </summary>
	public static bool IncludeCallerMethod = false;

	/// <summary>
	/// Indicates if caller class name should be included in the log message.
	/// </summary>
	public static bool IncludeCallerClass = false;

	/// <summary>
	/// The format to use for timestamps in log messages.
	/// </summary>
	public static string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

	/// <summary>
	/// The current minimum log level to record logs. Logs below this level will be ignored.
	/// </summary>
	public static LogLevel CurrentLogLevel = LogLevel.None;

	/// <summary>
	/// The size limit for the log file before log rotation occurs, in bytes.
	/// </summary>
	private static readonly long maxFileSize = 1024 * 1024 * 5; // 5 MB

	/// <summary>
	/// Keeps track of the current log file date.
	/// </summary>
	private static DateTime currentLogDate = DateTime.Now;

	/// <summary>
	/// A blocking collection used to queue log messages for asynchronous writing.
	/// </summary>
	private static readonly BlockingCollection<(LogLevel LogLevel, string Formatted, string Raw, int ThreadId, string Caller, string Method, string Timestamp)> logQueue = new();

	/// <summary>
	/// Token used to cancel the logging task when logging is stopped.
	/// </summary>
	private static readonly CancellationTokenSource cts = new();

	/// <summary>
	/// Indicates if the logging task is currently running.
	/// </summary>
	private static bool logTaskRunning = false;

	/// <summary>
	/// Options for the JSON serializer used to write logs to a JSON file.
	/// </summary>
	public static readonly JsonSerializerOptions JsonSerializerOptions = new() {
		WriteIndented = true,
	};

	/// <summary>
	/// Initializes the logger by creating the log file, setting up the initial log message,
	/// and starting the asynchronous logging task.
	/// </summary>
	[DebuggerStepThrough]
	public static void Initialize() {
		if (!Directory.Exists(LogFilesDirectory)) {
			Directory.CreateDirectory(LogFilesDirectory);
		}

		File.Create(LogFile).Close();
		File.Create(JsonLogFile).Close();

		fileWriter = new StreamWriter(LogFile, append: true);
		jsonWriter = new StreamWriter(JsonLogFile, append: true);

		StringBuilder sb = new();
		sb.AppendLine("------ [logs] ------");
		sb.AppendLine("--- [project configuration] ---");
		sb.AppendLine($" - Name: '{assemblyName.Value}'");
		sb.AppendLine($" - Version: '{assemblyVersion.Value}'");
		sb.AppendLine($" - Author: '{assemblyCompany.Value}'");
		sb.AppendLine($" - Build: '" +
#if DEBUG
		"Debug" +
#else
        "Release" +
#endif
		"'\n");

		WriteLine(LogLevel.None, sb.ToString());

		if (LogToJson) {
			jsonWriter.WriteLine("[");
		}

		AppDomain.CurrentDomain.ProcessExit += (sender, args) => StopLogging();
	}

	/// <summary>
	/// Starts the asynchronous log writing task, which reads from the log queue and writes logs to the file.
	/// </summary>
	[DebuggerStepThrough]
	private static void StartLogTask() {
		logTaskRunning = true;
		Task.Run(() => {
			foreach ((LogLevel LogLevel, string Formatted, string Raw, int ThreadId, string Caller, string Method, string Timestamp) logEntry in logQueue.GetConsumingEnumerable(cts.Token)) {
				try {
					lock (logLock) {
						if (LogToFile) WriteLogToFile(logEntry.Formatted);
						if (LogToConsole) Console.WriteLine(logEntry.Formatted);
						if (LogToJson) WriteLogToJsonFile(logEntry);
						if (LogToStream) {
							OutputStream.WriteLine(logEntry.Formatted);
							OutputStream.Flush();
						}
					}
				} catch { }
			}
		});
	}

	/// <summary>
	/// Stops the asynchronous log writing task by canceling the task's token.
	/// </summary>
	[DebuggerStepThrough]
	public static void StopLogging() {
		cts.Cancel();
		logTaskRunning = false;

		if (fileWriter is not null) {
			fileWriter.Flush();
			fileWriter.Close();
			fileWriter.Dispose();
			fileWriter = null;
		}

		if (jsonWriter is not null) {
			jsonWriter.WriteLine("\n]");
			jsonWriter.Flush();
			jsonWriter.Close();
			jsonWriter.Dispose();
			jsonWriter = null;
		}
	}

	/// <summary>
	/// Returns the formatted log file name based on the date and file number.
	/// </summary>
	/// <param name="date">The date of the log file.</param>
	/// <param name="fileNumber">The file number if multiple logs exist for the same date.</param>
	/// <returns>The formatted log file name.</returns>
	[DebuggerStepThrough]
	private static string GetLogFileName(DateTime date, int fileNumber) {
		string datePart = date.ToString("yyyy-MM-dd");
		return fileNumber == 0
			? Path.Combine(LogFilesDirectory, $"Log_{datePart}.log")
			: Path.Combine(LogFilesDirectory, $"Log_{datePart}_{fileNumber}.log");
	}

	/// <summary>
	/// Rotates the log file when the date changes or when it exceeds the size limit.
	/// </summary>
	[DebuggerStepThrough]
	private static void RotateLogsIfNeeded() {
		DateTime now = DateTime.Now;

		// Rotate if the date has changed
		if (now.Date != currentLogDate.Date) {
			currentLogDate = now;
			LogFile = GetLogFileName(now, 0);
		} else {
			// Rotate if the file size exceeds the limit
			CheckLogFileSizeAndRotate();
		}
	}

	/// <summary>
	/// Checks the log file size and rotates the log if it exceeds the size limit.
	/// </summary>
	[DebuggerStepThrough]
	private static void CheckLogFileSizeAndRotate() {
		if (!File.Exists(LogFile)) return;

		FileInfo fileInfo = new FileInfo(LogFile);
		if (fileInfo.Length >= maxFileSize) {
			int fileNumber = 0;

			// Find the next available file number for today's date
			while (File.Exists(GetLogFileName(currentLogDate, ++fileNumber))) { }

			LogFile = GetLogFileName(currentLogDate, fileNumber);
		}
	}

	/// <summary>
	/// Gathers context information such as the calling class, method name, and thread ID.
	/// </summary>
	/// <returns>A string containing context information for the log message.</returns>
	[DebuggerStepThrough]
	private static string GetContextInformation() {
		StackTrace stackTrace = new StackTrace(1, true);
		StackFrame? frame = null;

		// Iterate over the stack frames to find one that is not from the Log class
		for (int i = 0; i < stackTrace.FrameCount; i++) {
			frame = stackTrace.GetFrame(i);
			MethodBase? method = frame?.GetMethod();

			// Check if the method belongs to the Log class
			if (method?.DeclaringType?.Name != nameof(Log)) {
				break;
			}

			frame = null;
		}

		if (frame == null) {
			// If no valid frame was found, return an empty string or handle accordingly
			return string.Empty;
		}

		MethodBase? selectedMethod = frame.GetMethod();
		string className = IncludeCallerClass ? selectedMethod?.DeclaringType?.Name ?? "UnknownClass" : string.Empty;
		string methodName = IncludeCallerMethod ? selectedMethod?.Name ?? "UnknownMethod" : string.Empty;

		return IncludeContextInformation
			? $"[{className}.{methodName}] [Thread ID: {Environment.CurrentManagedThreadId}]"
			: string.Empty;
	}

	/// <summary>
	/// Prepares the log message by adding log level, timestamp, and context information (if enabled).
	/// </summary>
	/// <param name="logLevel">The log level of the message.</param>
	/// <param name="data">The message data to log.</param>
	/// <returns>A formatted log message string.</returns>
	[DebuggerStepThrough]
	private static string PrepareLogMessage(LogLevel logLevel, string[] data) {
		StringBuilder logBuilder = new();

		if (IncludeLogLevel) {
			logBuilder.Append($"[{logLevel}]");
		}

		if (IncludeTimestamp) {
			logBuilder.Append($" [{DateTime.Now.ToString(TimestampFormat)}]");
		}

		if (IncludeContextInformation) {
			logBuilder.Append($" {GetContextInformation()}");
		}

		logBuilder.Append($" - {string.Join(", ", data)}");
		return logBuilder.ToString();
	}

	#region WriteLine Overloads

	/// <summary>
	/// Writes a log message asynchronously, adding it to the log queue.
	/// </summary>
	/// <param name="logLevel">The log level of the message.</param>
	/// <param name="data">The message data to log.</param>
	[DebuggerStepThrough]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void WriteLine(LogLevel logLevel, params string[] data) {
		if (logLevel < CurrentLogLevel) return; // Log filtering by level

		RotateLogsIfNeeded(); // Check if a log rotation is needed before writing

		string raw = string.Join(", ", data);
		string logInfo = logLevel == LogLevel.None
			? raw
			: PrepareLogMessage(logLevel, data);
		string context = GetContextInformation();
		string caller;
		string method;
		try {
			caller = context.Split('.')[0].TrimStart('[');
			method = context.Split('.')[1].Split(']')[0];
		} catch {
			caller = "Unknown Caller";
			method = "Unknown Method";
		}
		string timestamp = DateTime.Now.ToString(TimestampFormat);
		int threadId = Environment.CurrentManagedThreadId;

		logQueue.Add((LogLevel: logLevel, Formatted: logInfo, Raw: raw, ThreadId: threadId, Caller: caller, Method: method, Timestamp: timestamp));

		if (!logTaskRunning) {
			StartLogTask();  // Ensure the log task is running
		}
	}

	/// <summary>
	/// Writes the specified object data to the log asynchronously, with default log level (None).
	/// </summary>
	/// <param name="data">The object data to log.</param>
	[DebuggerStepThrough]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteLine(params object[] data) {
		WriteLine(LogLevel.None, data.Select(d => d?.ToString() ?? "").ToArray());
	}

	/// <summary>
	/// Writes the specified string data to the log asynchronously, with default log level (None).
	/// </summary>
	/// <param name="data">The string data to log.</param>
	[DebuggerStepThrough]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteLine(params string[] data) {
		WriteLine(LogLevel.None, data);
	}

	#endregion

	#region WriteDebugLine Overloads

	/// <summary>
	/// Writes a log message asynchronously, adding it to the log queue.
	/// </summary>
	/// <param name="logLevel">The log level of the message.</param>
	/// <param name="data">The message data to log.</param>
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteDebugLine(LogLevel logLevel, params string[] data) {
		WriteLine(logLevel, data);
	}

	/// <summary>
	/// Writes the specified object data to the log asynchronously, with default log level (None).
	/// </summary>
	/// <param name="data">The object data to log.</param>
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteDebugLine(params string[] data) {
		WriteLine(LogLevel.Debug, data);
	}

	/// <summary>
	/// Writes the specified string data to the log asynchronously, with default log level (None).
	/// </summary>
	/// <param name="data">The string data to log.</param>
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteDebugLine(params object[] data) {
		WriteLine(LogLevel.Debug, data);
	}

	#endregion

	#region Conditional WriteLine Methods

	/// <summary>
	/// Conditionally writes a log message if the specified condition is true.
	/// </summary>
	/// <param name="condition">The condition to evaluate.</param>
	/// <param name="data">The message data to log if the condition is true.</param>
	[DebuggerStepThrough]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteLineIf(bool condition, params string[] data) {
		if (condition) {
			WriteLine(LogLevel.None, data);
		}
	}

	/// <summary>
	/// Conditionally writes a log message with the specified log level if the condition is true.
	/// </summary>
	/// <param name="logLevel">The log level of the message.</param>
	/// <param name="condition">The condition to evaluate.</param>
	/// <param name="data">The message data to log if the condition is true.</param>
	[DebuggerStepThrough]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteLineIf(LogLevel logLevel, bool condition, params string[] data) {
		if (condition) {
			WriteLine(logLevel, data);
		}
	}

	/// <summary>
	/// Conditionally writes a log message if the specified condition is true.
	/// </summary>
	/// <param name="condition">The condition to evaluate.</param>
	/// <param name="data">The message data to log if the condition is true.</param>
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteDebugLineIf(bool condition, params string[] data) {
		WriteDebugLineIf(LogLevel.None, condition, data);
	}

	/// <summary>
	/// Conditionally writes a log message with the specified log level if the condition is true.
	/// </summary>
	/// <param name="logLevel">The log level of the message.</param>
	/// <param name="condition">The condition to evaluate.</param>
	/// <param name="data">The message data to log if the condition is true.</param>
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void WriteDebugLineIf(LogLevel logLevel, bool condition, params string[] data) {
		WriteLineIf(logLevel, condition, data);
	}

	#endregion

	#region Error Handling

	/// <summary>
	/// Writes an error log message if the specified condition is true and throws an <see cref="Exception"/>.
	/// </summary>
	/// <param name="condition">The condition to evaluate.</param>
	/// <param name="error">The error message to log and include in the exception.</param>
	[MethodImpl(MethodImplOptions.NoInlining)]
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	public static void WriteErrorIf(bool condition, string error) {
		WriteErrorIf<Exception>(condition, error);
	}

	/// <summary>
	/// Writes an error log message and throws a specified exception type if the condition is true.
	/// </summary>
	/// <typeparam name="T">The type of exception to throw.</typeparam>
	/// <param name="condition">The condition to evaluate.</param>
	/// <param name="error">The error message to log and include in the exception.</param>
	/// <exception cref="T">The specified exception if the condition is true.</exception>
	[MethodImpl(MethodImplOptions.NoInlining)]
	[DebuggerStepThrough]
	[Conditional("DEBUG")]
	public static void WriteErrorIf<T>(bool condition, string error) where T : Exception {
		if (condition) {
			StackTrace stackTrace = new();
			WriteLine(LogLevel.Error, $"--- [error] ---\n - type: '{typeof(T).Name}'\n - message: '{error}'\n{stackTrace}\n");

			ConstructorInfo? stringConstructor = typeof(T).GetConstructor(new[] { typeof(string) });

			if (stringConstructor is not null)
				throw (T)(Activator.CreateInstance(typeof(T), error) ?? new Exception(error));

			throw new($"type='{typeof(T).Name}' with message='{error}'");
		}
	}

	#endregion

	/// <summary>
	/// Writes the log message to the current log file.
	/// </summary>
	/// <param name="message">The message to write to the file.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	[DebuggerStepThrough]
	private static void WriteLogToFile(string message) {
		fileWriter?.WriteLine(message);
		fileWriter?.Flush();
	}

	/// <summary>
	/// Writes the logEntry to the current log json file.
	/// </summary>
	/// <param name="logEntry">The logEntry to write to the file.</param>
	[DebuggerStepThrough]
	private static void WriteLogToJsonFile((LogLevel LogLevel, string Formatted, string Raw, int ThreadId, string Caller, string Method, string Timestamp) logEntry) {
		if (jsonWriter is null) return;

		var entry = new Dictionary<string, string>();

		if (IncludeTimestamp) {
			entry["Timestamp"] = DateTime.Now.ToString(TimestampFormat);
		}
		if (IncludeLogLevel) {
			entry["LogLevel"] = logEntry.LogLevel.ToString();
		}
		if (IncludeContextInformation) {
			entry["ThreadId"] = logEntry.ThreadId.ToString();
			entry["Caller"] = logEntry.Caller;
			entry["Method"] = logEntry.Method != "UnknownMethod" ?
				logEntry.Method.Split("<")[1].Split(">")[0]
				: logEntry.Method;
		}

		entry["Message"] = logEntry.Raw;

		// Serialize log entry to JSON string
		string jsonString = JsonSerializer.Serialize(entry, JsonSerializerOptions);

		// Prepend a tab character to each line of the JSON string
		string indentedJsonString = string.Join(Environment.NewLine, jsonString.Split(Environment.NewLine).Select(line => "\t" + line));

		// Write the JSON object with a leading comma if it's not the first object
		if (jsonWriter.BaseStream.Length > 1) {
			jsonWriter.WriteLine(",");
		}
		jsonWriter.Write(indentedJsonString);
		jsonWriter.Flush();
	}
}

/// <summary>
/// Class for profiling performance in terms of time taken for code execution.
/// </summary>
public static class Profiler {
	/// <summary>
	/// Options for profiling details.
	/// </summary>
	[Flags]
	public enum ProfilingOptions {
		None = 0,
		ElapsedTime = 1,
		MemoryUsage = 2,
		ThreadId = 4
	}

	/// <summary>
	/// Profiles the execution of a synchronous lambda function with optional profiling details.
	/// </summary>
	/// <typeparam name="TResult">The return type of the function.</typeparam>
	/// <param name="operationName">A descriptive name for the operation being profiled.</param>
	/// <param name="func">The lambda function to execute and profile.</param>
	/// <param name="options">Flags specifying which profiling details to include.</param>
	/// <returns>The result of the lambda function.</returns>
	public static TResult Profile<TResult>(string operationName, Func<TResult> func, ProfilingOptions options = ProfilingOptions.ElapsedTime) {
		Stopwatch stopwatch = Stopwatch.StartNew();
		TResult result;
		try {
			result = func();
		} catch (Exception ex) {
			Log.WriteLine(Log.LogLevel.Error, $"Error during '{operationName}': {ex}");
			throw;
		}
		stopwatch.Stop();

		StringBuilder profileMessage = new();
		if (options.HasFlag(ProfilingOptions.ElapsedTime)) {
			profileMessage.Append($"'{operationName}' completed in {stopwatch.ElapsedMilliseconds} ms");
		}

		if (options.HasFlag(ProfilingOptions.MemoryUsage)) {
			long memoryUsage = GC.GetTotalMemory(false);
			profileMessage.Append($" with memory usage of {memoryUsage} bytes");
		}

		if (options.HasFlag(ProfilingOptions.ThreadId)) {
			profileMessage.Append($" on Thread ID: {Environment.CurrentManagedThreadId}");
		}

		Log.WriteLine(Log.LogLevel.Info, profileMessage.ToString());
		return result;
	}

	/// <summary>
	/// Profiles the execution of an asynchronous lambda function with optional profiling details.
	/// </summary>
	/// <typeparam name="TResult">The return type of the async function.</typeparam>
	/// <param name="operationName">A descriptive name for the operation being profiled.</param>
	/// <param name="func">The asynchronous lambda function to execute and profile.</param>
	/// <param name="options">Flags specifying which profiling details to include.</param>
	/// <returns>The result of the lambda function as a Task.</returns>
	public static async Task<TResult> ProfileAsync<TResult>(string operationName, Func<Task<TResult>> func, ProfilingOptions options = ProfilingOptions.ElapsedTime) {
		Stopwatch stopwatch = Stopwatch.StartNew();
		TResult result;
		try {
			result = await func();
		} catch (Exception ex) {
			Log.WriteLine(Log.LogLevel.Error, $"Error during '{operationName}': {ex}");
			throw;
		}
		stopwatch.Stop();

		StringBuilder profileMessage = new();
		if (options.HasFlag(ProfilingOptions.ElapsedTime)) {
			profileMessage.Append($"'{operationName}' completed in {stopwatch.ElapsedMilliseconds} ms");
		}

		if (options.HasFlag(ProfilingOptions.MemoryUsage)) {
			long memoryUsage = GC.GetTotalMemory(false);
			profileMessage.Append($" with memory usage of {memoryUsage} bytes");
		}

		if (options.HasFlag(ProfilingOptions.ThreadId)) {
			profileMessage.Append($" on Thread ID: {Environment.CurrentManagedThreadId}");
		}

		Log.WriteLine(Log.LogLevel.Info, profileMessage.ToString());
		return result;
	}
}

/// <summary>
/// A class for redirecting logs to Visual Studio debug output.
/// </summary>
public class VSDebugStream : Stream {
	private readonly MemoryStream memoryStream = new MemoryStream();
	private readonly StreamWriter streamWriter;

	public VSDebugStream() {
		streamWriter = new StreamWriter(memoryStream, Encoding.UTF8, 1024, true);
	}

	public override void Write(byte[] buffer, int offset, int count) {
		memoryStream.Write(buffer, offset, count);
		// Flush to make sure all the bytes are written to memory stream
		memoryStream.Flush();
		// Move to the beginning of the memory stream to read from it
		memoryStream.Position = 0;
		using (var reader = new StreamReader(memoryStream, Encoding.UTF8, true, 1024, true)) {
			string output = reader.ReadToEnd();
			Debug.Write(output);
		}
		// Reset memory stream position
		memoryStream.SetLength(0);
		memoryStream.Position = 0;
	}

	// The following methods are not used but are required to override
	public override bool CanRead => false;
	public override bool CanSeek => false;
	public override bool CanWrite => true;
	public override long Length => memoryStream.Length;
	public override long Position { get => memoryStream.Position; set => memoryStream.Position = value; }
	public override void Flush() => memoryStream.Flush();
	public override int Read(byte[] buffer, int offset, int count) => memoryStream.Read(buffer, offset, count);
	public override long Seek(long offset, SeekOrigin origin) => memoryStream.Seek(offset, origin);
	public override void SetLength(long value) => memoryStream.SetLength(value);
}

/// <summary>
/// A stream fileWriter that writes to Visual Studio's debug output.
/// </summary>
public class VSDebugWriter : StreamWriter {
	public VSDebugWriter() : base(new VSDebugStream()) { }

	public VSDebugStream DebugStream => (VSDebugStream)BaseStream;
}

/// <summary>
/// A class that wraps data and masks sensitive information when calling ToString().
/// </summary>
public class MaskedData {
	private readonly object data;
	private readonly Func<object, string> maskingFunction;

	/// <summary>
	/// Initializes a new instance of the <see cref="MaskedData"/> class.
	/// </summary>
	/// <param name="data">The data to be masked.</param>
	/// <param name="maskingFunction">A function to mask the data when calling ToString().</param>
	public MaskedData(object data, Func<object, string> maskingFunction) {
		this.data = data ?? throw new ArgumentNullException(nameof(data));
		this.maskingFunction = maskingFunction ?? throw new ArgumentNullException(nameof(maskingFunction));
	}

	/// <summary>
	/// Returns the masked representation of the data.
	/// </summary>
	/// <returns>A masked string representation of the data.</returns>
	public override string ToString() {
		return maskingFunction(data);
	}
}

/// <summary>
/// A default implementation of <see cref="MaskedData"/> that shows only the class name of the object.
/// </summary>
public class MaskDataToClass : MaskedData {
	/// <summary>
	/// Initializes a new instance of the <see cref="DefaultMaskedData"/> class.
	/// </summary>
	/// <param name="data">The data to be represented with the class name.</param>
	public MaskDataToClass(object data)
		: base(data, FormatClassName) { }

	private static string FormatClassName(object data) {
		return $"[{data.GetType().Name} Instance]";
	}
}
