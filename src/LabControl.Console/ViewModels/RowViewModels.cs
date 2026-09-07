using CommunityToolkit.Mvvm.ComponentModel;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Protocol;

namespace LabControl.Console.ViewModels;

/// <summary>One row of the jobs panel: one job on one PC.</summary>
public sealed partial class JobRowViewModel : ObservableObject
{
    public JobRowViewModel(JobRecord job, int number)
    {
        Job = job;
        Id = job.Id;
        Created = DateTimeOffset.FromUnixTimeSeconds(job.CreatedAtUnix).ToLocalTime().ToString("T", Strings.Culture);
        Pc = number > 0 ? string.Format(Strings.Culture, Defaults.MachineNameFormat, number) : job.AgentId;
        Kind = Strings.Get("Job." + job.Kind);
        if (job.Kind == Shared.Protocol.Job.Types.Kind.RunScript && job.Args.TryGetValue(RunScriptRequest.NameKey, out var script) && script.Length > 0)
        {
            Kind = $"{Kind}: {script}";
        }

        Refresh();
    }

    public JobRecord Job { get; }

    public string Id { get; }

    public string Created { get; }

    public string Pc { get; }

    public string Kind { get; }

    [ObservableProperty]
    public partial string State { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Percent { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    public void Refresh()
    {
        State = Strings.Get("JobState." + Job.State);
        Percent = Job.Percent;
        Message = Job.Message;
        Output = string.Join(Environment.NewLine, Job.Output);
        IsFinished = Job.IsFinished;
        IsFailed = Job.State is JobState.Failed or JobState.TimedOut or JobState.NotDelivered;
    }
}

/// <summary>One row of the events panel.</summary>
public sealed class EventRowViewModel
{
    public EventRowViewModel(EventRecord record)
    {
        Record = record;
        Time = record.At.ToLocalTime().ToString("G", Strings.Culture);
        Severity = Strings.Get("Severity." + record.Severity);
        Pc = record.Number > 0 ? string.Format(Strings.Culture, Defaults.MachineNameFormat, record.Number) : string.Empty;
        Message = record.Message;
        IsWarning = record.Severity == EventSeverity.Warning;
        IsError = record.Severity == EventSeverity.Error;
    }

    public EventRecord Record { get; }

    public string Time { get; }

    public string Severity { get; }

    public string Pc { get; }

    public string Message { get; }

    public bool IsWarning { get; }

    public bool IsError { get; }
}

/// <summary>A banner across the top of the window: a sentence and, usually, one action.</summary>
public sealed partial class BannerViewModel : ObservableObject
{
    public BannerViewModel(string key, string text, string? actionLabel, Func<Task>? action, bool isWarning)
    {
        Key = key;
        Text = text;
        ActionLabel = actionLabel ?? string.Empty;
        HasAction = action is not null;
        Action = action;
        IsWarning = isWarning;
    }

    public string Key { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public string ActionLabel { get; }

    public bool HasAction { get; }

    public Func<Task>? Action { get; }

    public bool IsWarning { get; }
}
