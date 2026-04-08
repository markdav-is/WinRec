using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WinRec.Services;

public enum RecordingStatus
{
    Idle,
    Recording,
    Stopping
}

public class RecordingFile
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public long Size { get; set; }

    public string SizeFormatted => Size < 1_048_576
        ? $"{Size / 1024.0:F1} KB"
        : $"{Size / 1_048_576.0:F1} MB";

    public string DurationFormatted { get; set; } = string.Empty;
}

/// <summary>
/// Records the default microphone input AND default system audio (loopback)
/// simultaneously, then merges them into a single stereo WAV file:
///   Left  channel = microphone
///   Right channel = system audio (speakers / loopback)
/// </summary>
public class AudioRecordingService : IDisposable
{
    // Canonical output: 44 100 Hz · 16-bit · stereo
    private static readonly WaveFormat OutputFormat = new WaveFormat(44_100, 16, 2);

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;

    private string _micTempPath = string.Empty;
    private string _loopbackTempPath = string.Empty;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _loopbackWriter;

    private DateTime _recordingStart;
    private int _stoppedCount;   // counts how many captures have reported stopped

    // ──────────────────────────────────────────────────────────
    // Public state
    // ──────────────────────────────────────────────────────────
    public RecordingStatus Status { get; private set; } = RecordingStatus.Idle;
    public string RecordingsFolder { get; }
    public float MicLevel { get; private set; }
    public float SystemLevel { get; private set; }

    public TimeSpan Duration =>
        Status == RecordingStatus.Idle ? TimeSpan.Zero : DateTime.Now - _recordingStart;

    // ──────────────────────────────────────────────────────────
    // Events
    // ──────────────────────────────────────────────────────────
    public event Action? StateChanged;
    public event Action<float, float>? LevelsChanged;
    public event Action<string>? RecordingCompleted;

    public AudioRecordingService()
    {
        RecordingsFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WinRec", "Recordings");
        Directory.CreateDirectory(RecordingsFolder);
    }

    // ──────────────────────────────────────────────────────────
    // Start
    // ──────────────────────────────────────────────────────────
    public Task StartRecordingAsync()
    {
        if (Status != RecordingStatus.Idle)
            return Task.CompletedTask;

        _stoppedCount = 0;
        MicLevel = 0f;
        SystemLevel = 0f;

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _micTempPath = System.IO.Path.Combine(Path.GetTempPath(), $"winrec_mic_{timestamp}.wav");
        _loopbackTempPath = System.IO.Path.Combine(Path.GetTempPath(), $"winrec_sys_{timestamp}.wav");

        // Initialise microphone capture
        _micCapture = new WasapiCapture();
        _micWriter = new WaveFileWriter(_micTempPath, _micCapture.WaveFormat);
        _micCapture.DataAvailable += OnMicData;
        _micCapture.RecordingStopped += OnCaptureStopped;

        // Initialise loopback capture (system / speaker audio)
        _loopbackCapture = new WasapiLoopbackCapture();
        _loopbackWriter = new WaveFileWriter(_loopbackTempPath, _loopbackCapture.WaveFormat);
        _loopbackCapture.DataAvailable += OnLoopbackData;
        _loopbackCapture.RecordingStopped += OnCaptureStopped;

        _recordingStart = DateTime.Now;
        Status = RecordingStatus.Recording;
        StateChanged?.Invoke();

        _micCapture.StartRecording();
        _loopbackCapture.StartRecording();

        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────
    // Stop
    // ──────────────────────────────────────────────────────────
    public Task StopRecordingAsync()
    {
        if (Status != RecordingStatus.Recording)
            return Task.CompletedTask;

        Status = RecordingStatus.Stopping;
        StateChanged?.Invoke();

        _micCapture?.StopRecording();
        _loopbackCapture?.StopRecording();

        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────
    // Data handlers
    // ──────────────────────────────────────────────────────────
    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        MicLevel = CalculateLevel(e.Buffer, e.BytesRecorded, _micCapture!.WaveFormat);
        LevelsChanged?.Invoke(MicLevel, SystemLevel);
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs e)
    {
        _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        SystemLevel = CalculateLevel(e.Buffer, e.BytesRecorded, _loopbackCapture!.WaveFormat);
        LevelsChanged?.Invoke(MicLevel, SystemLevel);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        // Both captures must finish before we merge
        if (Interlocked.Increment(ref _stoppedCount) < 2)
            return;

        FinalizeRecording();
    }

    // ──────────────────────────────────────────────────────────
    // Finalize – flush, merge, clean up
    // ──────────────────────────────────────────────────────────
    private void FinalizeRecording()
    {
        // Flush writers BEFORE disposing captures so the WAV headers are correct
        _micWriter?.Dispose();   _micWriter = null;
        _loopbackWriter?.Dispose(); _loopbackWriter = null;

        _micCapture?.Dispose();      _micCapture = null;
        _loopbackCapture?.Dispose(); _loopbackCapture = null;

        string? outputPath = null;
        try
        {
            outputPath = MergeToStereo(_micTempPath, _loopbackTempPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinRec] merge error: {ex.Message}");
        }
        finally
        {
            TryDelete(_micTempPath);
            TryDelete(_loopbackTempPath);
        }

        Status = RecordingStatus.Idle;
        MicLevel = 0f;
        SystemLevel = 0f;
        StateChanged?.Invoke();

        if (outputPath != null)
            RecordingCompleted?.Invoke(outputPath);
    }

    /// <summary>
    /// Merges two mono/stereo WAV files into one stereo WAV.
    /// Left  = microphone, Right = system/loopback.
    /// Both sources are resampled to <see cref="OutputFormat"/> first.
    /// </summary>
    private string MergeToStereo(string micPath, string loopbackPath)
    {
        var timestamp = Path.GetFileNameWithoutExtension(micPath)
                            .Replace("winrec_mic_", string.Empty);
        var outputPath = Path.Combine(RecordingsFolder, $"Recording_{timestamp}.wav");

        using var micReader      = new WaveFileReader(micPath);
        using var loopbackReader = new WaveFileReader(loopbackPath);

        // Convert each source to 44100 Hz · float · mono, then merge as stereo
        ISampleProvider micSamples      = BuildConversionChain(micReader);
        ISampleProvider loopbackSamples = BuildConversionChain(loopbackReader);

        // Interleave as L/R stereo
        var stereoMix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2));

        // Wrap each mono source in a stereo panner so they go to their own channel
        var micLeft      = new PanningSampleProvider(micSamples)      { Pan = -1f }; // full left
        var loopbackRight = new PanningSampleProvider(loopbackSamples) { Pan =  1f }; // full right

        stereoMix.AddMixerInput(micLeft);
        stereoMix.AddMixerInput(loopbackRight);

        WaveFileWriter.CreateWaveFile16(outputPath, stereoMix);
        return outputPath;
    }

    /// <summary>Converts any WAV format to 44100 Hz · float · mono.</summary>
    private static ISampleProvider BuildConversionChain(WaveFileReader reader)
    {
        ISampleProvider samples = reader.ToSampleProvider();

        // Stereo → mono
        if (reader.WaveFormat.Channels == 2)
            samples = new StereoToMonoSampleProvider(samples);

        // Resample if needed
        if (reader.WaveFormat.SampleRate != 44_100)
            samples = new WdlResamplingSampleProvider(samples, 44_100);

        return samples;
    }

    // ──────────────────────────────────────────────────────────
    // Level calculation
    // ──────────────────────────────────────────────────────────
    private static float CalculateLevel(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        float peak = 0f;
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = format.BitsPerSample switch
            {
                16 => BitConverter.ToInt16(buffer, i * 2) / 32768f,
                32 => BitConverter.ToSingle(buffer, i * 4),
                _ => 0f
            };
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }

    // ──────────────────────────────────────────────────────────
    // Recordings list
    // ──────────────────────────────────────────────────────────
    public List<RecordingFile> GetRecordings()
    {
        if (!Directory.Exists(RecordingsFolder))
            return [];

        return [.. Directory.GetFiles(RecordingsFolder, "*.wav")
            .OrderByDescending(File.GetLastWriteTime)
            .Select(f =>
            {
                var info = new FileInfo(f);
                string duration = GetWavDuration(f);
                return new RecordingFile
                {
                    Path = f,
                    Name = Path.GetFileName(f),
                    CreatedAt = info.LastWriteTime,
                    Size = info.Length,
                    DurationFormatted = duration
                };
            })];
    }

    private static string GetWavDuration(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var ts = reader.TotalTime;
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }
        catch { return "??:??"; }
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _micCapture?.StopRecording();
        _loopbackCapture?.StopRecording();
        _micWriter?.Dispose();
        _loopbackWriter?.Dispose();
        _micCapture?.Dispose();
        _loopbackCapture?.Dispose();
    }
}
