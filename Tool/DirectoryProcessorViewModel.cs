using System.ComponentModel;

namespace Tool;

public class DirectoryProcessorViewModel : INotifyPropertyChanged {
	private string inputDirectory = "None";

	public string InputDirectory {
		get => inputDirectory;
		set {
			inputDirectory = value;
			OnPropertyChanged(nameof(InputDirectory));
		}
	}

	private string outputDirectory = "C:\\Users\\Vi\\Documents\\picture-work";

	public string OutputDirectory {
		get => outputDirectory;
		set {
			outputDirectory = value;
			OnPropertyChanged(nameof(OutputDirectory));
		}
	}

	private double progress = .0d;
	public double Progress {
		get => progress;
		set {
			progress = value;
			OnPropertyChanged(nameof(Progress));
		}
	}

	private bool isProcessing = false;
	public bool IsProcessing {
		get => isProcessing;
		set {
			isProcessing = value;
			OnPropertyChanged(nameof(IsProcessing));
		}
	}

	private string progressText = "";
	public string ProgressText {
		get => progressText;
		set {
			progressText = value;
			OnPropertyChanged(nameof(ProgressText));
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged(string propertyName) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
