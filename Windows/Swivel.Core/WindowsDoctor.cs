using System;
using System.Collections.Generic;
using System.Linq;

namespace Swivel.Core;

public enum DoctorAreaStatus { AvailableWithLimits, Planned }
public sealed record WindowsDoctorArea(string Id,string Title,string Description,DoctorAreaStatus Status,IReadOnlyList<string> ActionIds);

// Static symptom navigation only: constructing or selecting an area never starts a check.
// IDs are internal navigation keys, not new persisted actions or feature switches.
public static class WindowsDoctor
{
    public const string Intro="Choose a symptom, then start a check or review a window-close request explicitly. Windows Doctor adapts Mac Doctor's areas to Windows; results have separate limits and are not a complete health or security verdict.";
    public const string Scope="Browsing these areas does not scan. Checks remain read-only and optional; content comparisons require confirmation. Window-close requests require selecting one eligible window and a separate confirmation. No cleanup, uninstall, eject, administrator command or settings change runs here. Settings buttons only open the named Windows Settings page. Optional assistant requests require a full preview and explicit Send.";
    public const string Missing="Not implemented yet. This area is shown to make the remaining Windows work visible; it does not perform a check or offer a fix.";
    // The original basic snapshot is a Home-only, non-persisted entry, not a Catalog action.
    // Preserve that distinction so navigation does not expand My Wheel or assistant offerings.
    public static ToolAction ResolveAction(string id)=>id=="doctor.check"
        ?new("doctor.check","Run Read-only Checks",Feature.Doctor,RequiresFiles:false)
        :Catalog.Actions.Single(a=>a.Id==id&&a.Feature==Feature.Doctor);
    private static WindowsDoctorArea Area(string id,string title,string description,DoctorAreaStatus status,params string[] actions)=>new(id,title,description,status,Array.AsReadOnly(actions));
    public static IReadOnlyList<WindowsDoctorArea> Areas{get;}=Array.AsReadOnly(new[]{
        Area("summary","Summary","Start a one-time system, memory, fixed-drive, power, process and network-configuration snapshot. No automatic full scan or health score.",DoctorAreaStatus.AvailableWithLimits,"doctor.check"),
        Area("storage","Storage","Inspect recorded folder sizes or large files in a folder you choose. Sizes are logical and historical, not reclaimable bytes; Windows reserved storage, restore points and full System Data attribution are not covered.",DoctorAreaStatus.AvailableWithLimits,"doctor.folder","doctor.largefiles","doctor.commonfolders"),
        Area("caches","Caches","Review two Windows-reported temporary/legacy WinINET locations and explicitly measure one with the read-only size map. These are location hints, not safe-delete classifications; modern browser cache inventory, cleanup and reliable Recycle Bin undo remain incomplete.",DoctorAreaStatus.AvailableWithLimits,"doctor.caches"),
        Area("folders","Folder Sizes","Choose one local folder for a bounded metadata size map. Partial, inaccessible and skipped items stay visible. No file contents or deletion.",DoctorAreaStatus.AvailableWithLimits,"doctor.folder","doctor.commonfolders"),
        Area("duplicates","Duplicate & Similar Files","Compare bounded local contents after confirmation. Exact hashes, visual similarity and text similarity have different meanings; none is automatic deletion advice. Removal and reliable undo are still planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.duplicates","doctor.commonduplicates","doctor.photosimilar","doctor.textsimilar","doctor.commonphotosimilar","doctor.commontextsimilar"),
        Area("projects","Project Builds","Find bounded build-folder hints from nearby project marker names and move chosen ones to the Recycle Bin after a recheck, with Undo. Configurations are not parsed; custom build paths are not found.",DoctorAreaStatus.AvailableWithLimits,"doctor.projects"),
        Area("performance","Slow or Hot","Monitor system CPU and physical memory, inspect reported executable-path working-set groups, or separately review one window for a normal close request. These do not establish helper ownership, leaks, heat, memory release or the cause of a slowdown.",DoctorAreaStatus.AvailableWithLimits,"doctor.monitor","doctor.appmemory","doctor.closewindow"),
        Area("app","Apps That Will Not Open","Choose a local EXE for bounded header declarations. Processor and certificate-table fields do not prove run compatibility or signature trust. Runtime dependencies, SmartScreen, full signature checks and repair are still planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.executable"),
        Area("uninstall","Uninstall Apps","Inspect desktop installation records only. An installation record is not a safe removal plan. Uninstalling, leftover ownership checks and reversible cleanup are not implemented.",DoctorAreaStatus.AvailableWithLimits,"doctor.installedapps"),
        Area("drives","External Drives","Inspect mounted drive-letter types, filesystem declarations, caller capacity and read-only flags. Drive type does not identify external hardware; unmounted devices, USB identity, write-access diagnosis and safe eject remain planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.drives"),
        Area("network","Network","Read adapter IP, DNS, gateway and current-user proxy configuration in the basic snapshot. Addresses stay hidden by default. No reachability, speed, DNS or proxy-script request; effective app-specific proxy diagnosis remains incomplete.",DoctorAreaStatus.AvailableWithLimits,"doctor.check"),
        Area("background","Background Items","Inspect Run/RunOnce records, raw StartupApproved values, local service status, selected Startup-folder entries or bounded local scheduled-task metadata. These do not prove effective enablement, trust, task ownership or leftover ownership. StartupApproved decoding, disabling and removal are still planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.startup","doctor.startupapproved","doctor.services","doctor.startupfolders","doctor.scheduledtasks"),
        Area("power","Battery & Sleep","Inspect reported power state, captured-plan plugged-in/battery timeouts and a separate sleep-capability sample. Capabilities do not guarantee a state is usable now; battery wear, sleep blockers and sleep history are not diagnosed, and policy or Modern Standby can change effective behavior.",DoctorAreaStatus.AvailableWithLimits,"doctor.power","doctor.sleepstates","doctor.check")
    });
}
