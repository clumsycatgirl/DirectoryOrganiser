using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

using Microsoft.Win32;

namespace Tool;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window {
	private readonly List<string> inputDirectories = new();
	public DirectoryProcessorViewModel ViewModel { get; set; }

	static MainWindow() {
		Log.IncludeContextInformation = false;
		Log.IncludeCallerClass = Log.IncludeCallerMethod = false;
		Log.LogFilesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Michelon", "Pictures Tools");
	}

	public MainWindow() {
		InitializeComponent();
		ViewModel = new DirectoryProcessorViewModel();
		DataContext = ViewModel;
	}

	private List<string>? OpenFolderDialog(bool multiselect) {
		OpenFolderDialog ofd = new() {
			Multiselect = multiselect,
			ValidateNames = true,

		};

		bool result = ofd.ShowDialog() ?? false;

		return multiselect
			? result
				? ([.. ofd.FolderNames])
				: null
			: result
				? [ofd.FolderName]
				: null;
	}

	private void ProcessDirectories(List<string> directories) {
		Dispatcher.Invoke(() => ViewModel.ProgressText = "Processing Files");
		int totalFiles = directories.Sum(directory => GetTotalFilesInDirectory(directory));

		int processedFiles = 0;

		directories.ForEach(directory => {
			Log.WriteLine(Log.LogLevel.Info, $"Processing {directory}");
			ProcessDirectory(directory, totalFiles, ref processedFiles);
			Log.WriteLine(Log.LogLevel.Info, $"Done processing {directory}");
		});

		Dispatcher.Invoke(() => ViewModel.Progress = 100);
	}

	private void ProcessDirectory(string directory, int totalFiles, ref int processedFiles, string relativePath = "") {
		if (directory.StartsWith('.')) {
			Log.WriteLine(Log.LogLevel.Info, $"Skipping directory {directory}");
			return;
		}

		DirectoryInfo di = new(directory);

		if (!di.Exists) {
			Log.WriteLine(Log.LogLevel.Error, $"Directory {directory} does not exist");
			return;
		}

		Log.WriteLine(Log.LogLevel.Info, $"Directory: {directory}");
		FileInfo[] files = di.GetFiles();

		foreach (FileInfo file in files) {
			ProcessFile(file, relativePath, totalFiles, ref processedFiles);
		}

		DirectoryInfo[] subDirectories = di.GetDirectories();

		foreach (DirectoryInfo subDirectory in subDirectories) {
			if ((subDirectory.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) {
				continue;
			}

			ProcessDirectory(subDirectory.FullName, totalFiles, ref processedFiles, Path.Combine(relativePath, subDirectory.Name));
		}
	}

	private void ProcessFile(FileInfo file, string relativePath, int totalFiles, ref int processedFiles) {
		// Skip hidden files
		if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) {
			return;
		}

		if (!new List<string>([".png", ".jpg", ".mp3", ".mp4", ".epub", ".pdf", ".docx", ".doc", ".gif", ".zip", ".htm", ".html", ".css", ".opus", ".m4a", ".avi"]).Contains(file.Extension.ToLower())) {
			Log.WriteLine(Log.LogLevel.Info, $"Skipping {file.FullName}");
			goto end;
		}

		Log.WriteLine(Log.LogLevel.Info, $"Processing {file.FullName}");
		DateTime creationTime = file.CreationTime;
		DateTime lastUpdateTime = file.LastWriteTime;

		DateTime earlierTime = creationTime < lastUpdateTime ? creationTime : lastUpdateTime;

		string year = earlierTime.Year.ToString();
		string monthNumber = earlierTime.Month.ToString();
		string monthString = earlierTime.ToString("MMMM", new CultureInfo("it-IT"));
		string month = $"{monthNumber}.{monthString}";

		string relativeFilePath = Path.Combine(relativePath, file.Name);
		string? originalDirectory = Path.GetDirectoryName(relativeFilePath);
		if (originalDirectory is null || !new Regex("[a-z]", RegexOptions.Compiled).IsMatch(originalDirectory)) {
			originalDirectory = "";
		}
		originalDirectory = originalDirectory.Replace('\\', '/');

		string destinationDirectory = Path.Combine(ViewModel.OutputDirectory, year, month, originalDirectory);
		destinationDirectory = Path.GetFullPath(destinationDirectory);
		if (!Directory.Exists(destinationDirectory)) {
			Log.WriteLine(Log.LogLevel.Info, $"Creating directory {destinationDirectory}");
			Directory.CreateDirectory(destinationDirectory);
		}

		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
		string extension = Path.GetExtension(file.Name).ToLower();
		string fileNameWithExtension = fileNameWithoutExtension + extension;
		string destinationFile = Path.Combine(destinationDirectory, fileNameWithExtension);

		if (File.Exists(destinationFile)) {
			if (AreFilesIdentical(file.FullName, destinationFile)) {
				Log.WriteLine(Log.LogLevel.Info, $"Skipping file {file.FullName} as it already exists in destination with the same content.");
				goto end;
			} else {
				int counter = 0;
				while (File.Exists(destinationFile)) {
					destinationFile = Path.Combine(destinationDirectory, $"{Path.GetFileNameWithoutExtension(file.Name)}_{++counter}{file.Extension}");
				}
			}
		}

		if (IsValidFileName(fileNameWithExtension) is string error) {
			Log.WriteLine(Log.LogLevel.Error, $"Invalid file name {fileNameWithExtension}; error={error}");
			goto end;
		}

		try {
			file.CopyTo(destinationFile);
		} catch { }

		Log.WriteLine(Log.LogLevel.Info, $"Copied {file.FullName} to {destinationFile}");

	end:
		processedFiles++;
		double progressValue = (double)processedFiles / totalFiles * 100;
		ViewModel.ProgressText = $"{processedFiles} / {totalFiles}";

		Dispatcher.Invoke(() => ViewModel.Progress = progressValue);
	}

	private void OpenInputButton_Click(object sender, RoutedEventArgs e) {
		List<string>? directories = OpenFolderDialog(true);

		if (directories is null) {
			return;
		}

		inputDirectories.Clear();
		inputDirectories.AddRange(directories);
		ViewModel.InputDirectory = string.Join(',', directories);
	}

	private void OpenOutputButton_Click(object sender, RoutedEventArgs e) {
		List<string>? directories = OpenFolderDialog(false);

		if (directories is null) {
			return;
		}

		ViewModel.OutputDirectory = directories[0];
	}

	private async void StartProcessingButton_Click(object sender, RoutedEventArgs e) {
		if (inputDirectories.Count == 0) {
			MessageBox.Show("Please select input directories first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}

		ViewModel.IsProcessing = true;
		ViewModel.Progress = .0d;

		try {
			await Task.Run(() => ProcessDirectories(inputDirectories));
		} finally {
			MessageBox.Show("Processing completed", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
			ViewModel.IsProcessing = false;
		}
	}

	private int GetTotalFilesInDirectory(string directory) {
		DirectoryInfo di = new DirectoryInfo(directory);

		if (!di.Exists) return 0;

		int fileCount = 0;

		try {
			FileInfo[] files = di.GetFiles();
			fileCount += files.Count(file => (file.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden);

			DirectoryInfo[] subDirectories = di.GetDirectories();
			foreach (DirectoryInfo subDirectory in subDirectories) {
				// Skip hidden directories
				if ((subDirectory.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) {
					continue;
				}

				fileCount += GetTotalFilesInDirectory(subDirectory.FullName);
			}
		} catch (Exception) { }

		return fileCount;
	}

	private string? IsValidFileName(string fileName) {
		string invalidChars = new string(Path.GetInvalidFileNameChars());
		if (fileName.Any(ch => invalidChars.Contains(ch))) {
			return $"invalid character error '{fileName.First(ch => invalidChars.Contains(ch))}'";
		}

		string[] reservedNames = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
		string baseName = Path.GetFileNameWithoutExtension(fileName).ToUpper();
		if (reservedNames.Contains(baseName)) {
			return "reserved name error";
		}

		const int maxFileNameLength = 255;
		if (fileName.Length > maxFileNameLength) {
			return "length error";
		}

		return null;
	}

	private bool AreFilesIdentical(string filePath1, string filePath2) {
		const int bufferSize = 1024 * 1024; // 1MB buffer size
		byte[] buffer1 = new byte[bufferSize];
		byte[] buffer2 = new byte[bufferSize];

		using FileStream fs1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read);
		using FileStream fs2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read);

		int bytesRead1;
		int bytesRead2;

		do {
			bytesRead1 = fs1.Read(buffer1, 0, bufferSize);
			bytesRead2 = fs2.Read(buffer2, 0, bufferSize);

			if (bytesRead1 != bytesRead2 || !buffer1.Take(bytesRead1).SequenceEqual(buffer2.Take(bytesRead2))) {
				return false;
			}
		} while (bytesRead1 > 0);

		return true;
	}
}
