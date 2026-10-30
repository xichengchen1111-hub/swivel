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
    public const string Scope="Browsing these areas does not scan. Checks start only from their button and only read; content comparisons require confirmation. Items are moved to the Recycle Bin only from a results window, when you select them, after a recheck and a confirmation, with Undo while that window is open. Window-close requests require selecting one eligible window and a separate confirmation. No uninstall, eject, administrator command or settings change runs here. Settings buttons only open the named Windows Settings page. Optional assistant requests require a full preview and explicit Send.";
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
        Area("caches","Caches","Review the Windows temporary and legacy WinINET folders, or the default cache folders of known browsers and apps. Old temporary items and cache folders of closed apps can be moved to the Recycle Bin after a recheck, with Undo. These are fixed location lists, not safe-delete classifications; Store apps, portable copies and custom cache folders are not found.",DoctorAreaStatus.AvailableWithLimits,"doctor.caches","doctor.appcaches"),
        Area("folders","Folder Sizes","Choose one local folder for a bounded metadata size map. Partial, inaccessible and skipped items stay visible. No file contents or deletion.",DoctorAreaStatus.AvailableWithLimits,"doctor.folder","doctor.commonfolders"),
        Area("duplicates","Duplicate & Similar Files","Compare bounded local contents after confirmation. Exact hashes, visual similarity and text similarity have different meanings; none is automatic deletion advice. Removal and reliable undo are still planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.duplicates","doctor.commonduplicates","doctor.photosimilar","doctor.textsimilar","doctor.commonphotosimilar","doctor.commontextsimilar"),
        Area("projects","Project Builds","Find bounded build-folder hints from nearby project marker names and move chosen ones to the Recycle Bin after a recheck, with Undo. Configurations are not parsed; custom build paths are not found.",DoctorAreaStatus.AvailableWithLimits,"doctor.projects"),
        Area("performance","Slow or Hot","Monitor system CPU and physical memory, inspect reported executable-path working-set groups, or separately review one window for a normal close request. These do not establish helper ownership, leaks, heat, memory release or the cause of a slowdown.",DoctorAreaStatus.AvailableWithLimits,"doctor.monitor","doctor.appmemory","doctor.closewindow"),
        Area("app","Apps That Will Not Open","Choose a local EXE for bounded header declarations. Processor and certificate-table fields do not prove run compatibility or signature trust. Runtime dependencies, SmartScreen, full signature checks and repair are still planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.executable"),
        Area("uninstall","Clean Up Files","Find caches, old temporary items, installers and project build folders in one list, tick what you no longer need and move it to the Recycle Bin, with Undo. Or inspect desktop installation records, uninstall through Installed Apps in Windows Settings, then find plain folders named exactly as apps removed since the list was read and move chosen ones to the Recycle Bin, with Undo. A matching name does not prove ownership; registry entries, services, scheduled tasks and Store apps are not cleaned.",DoctorAreaStatus.AvailableWithLimits,"doctor.cleanup","doctor.installedapps"),
        Area("drives","External Drives","Inspect mounted drive-letter types, filesystem declarations, caller capacity and read-only flags. Drive type does not identify external hardware; unmounted devices, USB identity, write-access diagnosis and safe eject remain planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.drives"),
        Area("network","Network","Read adapter IP, DNS, gateway and current-user proxy configuration in the basic snapshot; addresses stay hidden by default. Test Internet Connection opens a window whose buttons contact fixed Microsoft servers to check the gateway, name resolution, sign-in pages and secure connections, and optionally sample download speed. No repair; effective app-specific proxy diagnosis remains incomplete.",DoctorAreaStatus.AvailableWithLimits,"doctor.check","doctor.nettest"),
        Area("background","Background Items","Inspect Run/RunOnce records, raw StartupApproved values, startup apps with their observed Task Manager approval state, local service status, selected Startup-folder entries or bounded local scheduled-task metadata. These do not prove effective enablement, trust, task ownership or leftover ownership. Disabling and removal are still planned.",DoctorAreaStatus.AvailableWithLimits,"doctor.startupapps","doctor.startup","doctor.startupapproved","doctor.services","doctor.startupfolders","doctor.scheduledtasks"),
        Area("power","Battery & Sleep","Inspect reported power state, captured-plan plugged-in/battery timeouts and a separate sleep-capability sample. Capabilities do not guarantee a state is usable now; battery wear, sleep blockers and sleep history are not diagnosed, and policy or Modern Standby can change effective behavior.",DoctorAreaStatus.AvailableWithLimits,"doctor.power","doctor.sleepstates","doctor.check")
    });
}
