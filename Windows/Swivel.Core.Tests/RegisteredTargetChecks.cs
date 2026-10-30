using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Swivel.Core;

internal static class RegisteredTargetChecks
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        var env = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["APPHOME"] = @"C:\Apps\Foo" };
        string? Parse(string text) => RegisteredTargets.Executable(text, false, env);
        check(Parse("\"C:\\Apps\\Foo\\foo.exe\" --flag \"arg value\"") == @"C:\Apps\Foo\foo.exe", "Quoted executable separated from arguments");
        check(Parse(@"C:\Apps\Foo\foo.exe --arg=C:\Outside\x.exe") == @"C:\Apps\Foo\foo.exe", "Arguments never establish executable ownership");
        check(Parse(@"%apphome%\foo.exe --flag") == @"C:\Apps\Foo\foo.exe", "Bounded explicit environment expansion");
        foreach (var text in new[] { @"C:\Program Files\Foo\foo.exe --flag", @"foo.exe", @".\foo.exe", @"C:foo.exe", @"C:\APPS~1\foo.exe", @"C:\Apps\Foo\..\other.exe", @"%UNKNOWN%\foo.exe", "\"C:\\Apps\\foo.exe", "\"C:\\Apps\\foo.exe\"suffix", new string('a', 32768), "C:\\Apps\\foo.exe\0 --x", "C:\\Apps\\foo.exe \"unclosed" })
            check(Parse(text) is null, "Unresolved commands are never guessed: " + text[..Math.Min(40,text.Length)]);
        check(Parse("\"C:\\Apps\\😀\\foo.exe\" --arg=文字") == @"C:\Apps\😀\foo.exe" && RegisteredTargets.Contains(@"C:\Apps\😀", @"C:\Apps\😀\foo.exe"), "Valid Unicode surrogate pairs remain supported in normalized paths");
        check(Parse("C:\\Apps\\\ud800.exe") is null, "Malformed UTF16 command remains unresolved");
        var variableSpaces = new Dictionary<string,string> { ["PROGRAMFILES"] = @"C:\Program Files" };
        check(RegisteredTargets.Executable(@"%PROGRAMFILES%\Foo\a.exe --x", false, variableSpaces) is null, "Unquoted environment expansion that introduces spaces stays ambiguous");
        check(RegisteredTargets.Executable("\"%PROGRAMFILES%\\Foo\\a.exe\" --x", false, variableSpaces) == @"C:\Program Files\Foo\a.exe", "Quoted environment expansion preserves executable boundary");
        check(RegisteredTargets.Executable(@"C:\Program Files\Foo\foo.exe", true, env) == @"C:\Program Files\Foo\foo.exe", "Task/App Paths literal path may contain spaces");
        check(RegisteredTargets.Contains(@"c:\APPS\foo", @"C:\Apps\Foo\bin\foo.exe") && !RegisteredTargets.Contains(@"C:\Apps\Foo", @"C:\Apps\FooBar\foo.exe"), "Case-insensitive component boundary");
        check(!RegisteredTargets.Contains(@"C:\Apps\Foo", @"C:\Apps\Foo\..\FooBar\a.exe") && !RegisteredTargets.Contains(@"C:\APPS~1", @"C:\APPS~1\a.exe"), "Invalid and short roots cannot claim containment");
        string maxCommand = @"C:\Apps\Foo\a.exe " + new string('x', RegisteredTargets.MaxCommand - @"C:\Apps\Foo\a.exe ".Length);
        check(Parse(maxCommand) == @"C:\Apps\Foo\a.exe" && Parse(maxCommand + "x") is null, "Exact command-unit bound accepted and plus one unresolved");
        string maxPath = @"C:\" + new string('x', 233) + ".exe";
        check(RegisteredTargets.Executable(maxPath, true, env) == maxPath && RegisteredTargets.Executable(maxPath + "e", true, env) is null, "Exact local path bound accepted and plus one refused");
        var oversizedEnvironment = new Dictionary<string,string> { ["HUGE"] = new string('x', 4096) };
        check(RegisteredTargets.Executable(@"%HUGE%\a.exe", false, oversizedEnvironment) is null, "Hostile expanded environment values bounded without recursive expansion");
        var raw = new[] { new RegisteredCommand("Services", "Foo\u202e", @"C:\Apps\Foo\a.exe"), new RegisteredCommand("Run", "Other", @"C:\Apps\FooBar\a.exe"), new RegisteredCommand("Run", "Bad", "relative.exe") };
        var snapshot = RegisteredTargets.Resolve(raw, Array.Empty<string>(), env, default);
        var result = RegisteredTargets.ForFolder(snapshot, @"C:\Apps\Foo", default);
        check(result.Matches.Count == 1 && result.Unresolved.Count == 1 && !result.CanTrash, "Matches upgrade evidence; unresolved commands remain visible and refuse Trash");
        check(RegisteredTargets.ForFolder(RegisteredTargets.Resolve(raw.Take(2), Array.Empty<string>(), env, default), @"C:\Apps\FooBarBar", default).CanTrash, "Sibling app names are not evidence");
        reject(() => RegisteredTargets.RequireTrash(result), "Registered target prevents move-time Trash");
        reject(() => RegisteredTargets.RequireTrash(RegisteredTargets.ForFolder(new(Array.Empty<RegisteredTarget>(), new[]{"couldn't read"}), @"C:\Apps\Foo", default)), "Incomplete sources refuse cleanup");
        reject(() => RegisteredTargets.Resolve(raw, Array.Empty<string>(), env, new CancellationToken(true)), "Precancel matching");
        using var cancel = new CancellationTokenSource();
        IEnumerable<RegisteredCommand> Cancelled() { yield return raw[0]; cancel.Cancel(); yield return raw[1]; }
        reject(() => RegisteredTargets.Resolve(Cancelled(), Array.Empty<string>(), env, cancel.Token), "Mid-enumeration cancellation");
        var unsafeReport = RegisteredTargets.Report(result, x => x);
        check(unsafeReport.Contains("Foo[U+202E]") && unsafeReport.Contains(RegisteredTargets.Unresolved), "Visible-text sanitizing for evidence and unresolved status");
        var argv = RegisteredTargets.ForFolder(RegisteredTargets.Resolve(new[] { new RegisteredCommand("Run", "app", "C:\\Apps\\Foo\\a.exe --secret=PRIVATE") }, Array.Empty<string>(), env, default), @"C:\Apps\Foo", default);
        check(!RegisteredTargets.Report(argv, x => x).Contains("PRIVATE"), "Arguments do not leak into evidence reports");
        foreach (var source in new[] { RegisteredTargets.RunFailed, RegisteredTargets.ServicesFailed, RegisteredTargets.TasksFailed, RegisteredTargets.AppPathsFailed, RegisteredTargets.ClassesFailed })
        {
            var denied = RegisteredTargets.ForFolder(new(Array.Empty<RegisteredTarget>(), new[] { source }), @"C:\Apps\Foo", default);
            check(!denied.CanTrash && RegisteredTargets.Report(denied, x => x).Contains(source), "Source-specific unreadable state is not an empty inventory");
        }
        check(!RegisteredTargets.ForFolder(snapshot, null, default).CanTrash && RegisteredTargets.Report(RegisteredTargets.ForFolder(snapshot, null, default), x => x).Contains(RegisteredTargets.NoFolder), "Missing install folder stays unknown with explicit alternative");
        var many = RegisteredTargets.Resolve(Enumerable.Repeat(raw[0], RegisteredTargets.MaxRows + 1), Array.Empty<string>(), env, default);
        check(many.Targets.Count == RegisteredTargets.MaxRows && many.Issues.Count > 0, "Combined matching row cap reports partial");
        check(RegisteredTargets.Resolve(new[]{new RegisteredCommand("Run", new string('x', 5000), "bad")}, Array.Empty<string>(), env, default).Targets.Single().Name.Length < 5000, "Hostile source names bounded");
    }
}
