using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Swivel.Core;

public enum DoctorLabelKind { DisplayedProcess,ExecutableBasename,ReportedApp,StartupValue }
public sealed record DoctorLabel(int SourceIndex,DoctorLabelKind Kind,string Text);
public sealed class DoctorLabelSet
{
    public IReadOnlyList<DoctorLabel> Rows{get;}
    public int Missing{get;}
    public int Invalid{get;}
    public int Duplicates{get;}
    internal DoctorLabelSet(DoctorLabel[] rows,int missing,int invalid,int duplicates){Rows=Array.AsReadOnly(rows);Missing=missing;Invalid=invalid;Duplicates=duplicates;}
}

// Explicit optional metadata, kept separately from the default redacted JSON.
// No filesystem/registry reads or fallback to identifiers, paths or commands.
public static class DoctorLabels
{
    public const string Limit="Select 1–40 reported labels, each up to 160 UTF-16 units on one line, without whitespace-only values, control/format characters or line separators. The complete report and service request must each fit 32 KiB; nothing is shortened.";
    private sealed record Slot(int SourceIndex,DoctorLabelKind Kind,string? Text);
    private static void Require(bool valid){if(!valid)throw new UserError(Limit);}
    private static bool Valid(string text)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>160)return false;
        for(int i=0;i<text.Length;)
        {
            if(Rune.DecodeFromUtf16(text.AsSpan(i),out var rune,out int used)!=OperationStatus.Done||Rune.IsControl(rune)||Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)return false;
            i+=used;
        }
        return true;
    }
    private static DoctorLabelSet Build(IEnumerable<Slot> slots,int max)
    {
        var rows=new List<DoctorLabel>();var seen=new HashSet<(DoctorLabelKind,string)>();int missing=0,invalid=0,duplicates=0,count=0;
        foreach(var row in slots)
        {
            Require(++count<=max);if(row.Text is null){missing++;continue;}if(!Valid(row.Text)){invalid++;continue;}
            if(!seen.Add((row.Kind,row.Text))){duplicates++;continue;}rows.Add(new(row.SourceIndex,row.Kind,row.Text));
        }
        return new(rows.ToArray(),missing,invalid,duplicates);
    }
    public static DoctorLabelSet Facts(IReadOnlyList<DoctorFact> facts)
    {
        Require(facts.Count<=512);var slots=new List<Slot>();
        for(int i=0;i<facts.Count;i++)
        {var f=facts[i];if(f is not null&&!f.PrivateDetails&&f.Section=="Overview"&&f.Title=="Largest working sets"&&f.Format=="{0}: {1} MB resident memory")slots.Add(new(i,DoctorLabelKind.DisplayedProcess,f.Values is {Length:2}&&f.Values[0] is string name?name:null));}
        return Build(slots,512);
    }
    public static DoctorLabelSet Memory(AppMemorySnapshot snapshot)
    {
        Require(snapshot.Processes.Count<=4096);
        return Build(snapshot.Processes.Select((p,i)=>new Slot(i,DoctorLabelKind.ExecutableBasename,p is not null&&(p.Issues&AppMemoryIssue.PathUnavailable)==0?WindowsBasename(p.Path):null)),4096);
    }
    public static DoctorLabelSet Installed(InstalledAppsSnapshot snapshot)
    {
        Require(snapshot.Records.Count<=4096);
        return Build(snapshot.Records.Select((r,i)=>new Slot(i,DoctorLabelKind.ReportedApp,r?.Name is {State:RegistrationValueState.Present,Value:string name}?name:null)),4096);
    }
    public static DoctorLabelSet Startup(StartupSnapshot snapshot)
    {
        Require(snapshot.Records.Count<=2048);
        return Build(snapshot.Records.Select((r,i)=>new Slot(i,DoctorLabelKind.StartupValue,r is not null&&(r.Issues&StartupIssue.InvalidName)==0?r.Name:null)),2048);
    }
    private static string? WindowsBasename(string? source)
    {
        if(string.IsNullOrEmpty(source)||source.Length>4096||source.Contains('/')||source.EndsWith('\\'))return null;
        string path=source;
        if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))path=@"\\"+path[8..];
        else if(path.StartsWith(@"\\?\",StringComparison.Ordinal))path=path[4..];
        bool drive=path.Length>=4&&char.IsAsciiLetter(path[0])&&path[1]==':'&&path[2]=='\\';
        bool unc=path.StartsWith(@"\\",StringComparison.Ordinal)&&!path.StartsWith(@"\\.\",StringComparison.Ordinal)&&!path.StartsWith(@"\\?\",StringComparison.Ordinal);
        if(!drive&&!unc)return null;
        string[] parts=path[(drive?3:2)..].Split('\\');if(parts.Length<(drive?1:3))return null;
        try{foreach(string part in parts)WindowsPathPolicy.ValidateName(part);}catch(UserError){return null;}
        string name=parts[^1];return Valid(name)?name:null;
    }
    public static DoctorReportSnapshot Include(DoctorReportSnapshot snapshot,DoctorLabelSet labels,IReadOnlyList<int> selected)
    {
        var indices=selected.ToArray();Require(indices.Length is >=1 and <=40&&indices.Distinct().Count()==indices.Length&&indices.All(i=>i>=0&&i<labels.Rows.Count));
        string Kind(DoctorLabelKind kind)=>kind switch{DoctorLabelKind.DisplayedProcess=>"displayed_process_label",DoctorLabelKind.ExecutableBasename=>"reported_executable_basename",DoctorLabelKind.ReportedApp=>"reported_app_name",DoctorLabelKind.StartupValue=>"reported_startup_value_name",_=>throw new UserError(Limit)};
        using var report=JsonDocument.Parse(snapshot.Json);
        string json=JsonSerializer.Serialize(new{format="Swivel.Doctor.SelectedLabels.v1",report=report.RootElement,
            selected_reported_labels=indices.Select(i=>new{kind=Kind(labels.Rows[i].Kind),text=labels.Rows[i].Text}),
            limitations="Labels are an explicitly selected subset of already-read, untrusted reported metadata, not identities linked to anonymous metrics. They can contain paths, addresses or other private text. No versions, publishers, identifiers, commands or file contents are added automatically. Displayed basic-process labels may already be shortened by the source report. The underlying numeric report remains complete within its recorded bounds."},new JsonSerializerOptions{WriteIndented=true});
        Require(Encoding.UTF8.GetByteCount(json)<=32768);return new(json,snapshot.SingleSample);
    }
}
