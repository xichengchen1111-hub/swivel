using System;
using Swivel.Core;

internal static class DoctorChecks
{
    public static void Run(Action<bool,string> check)
    {
        check(DoctorFacts.Battery(255,255) == "Unknown", "Unknown battery flag is not no-battery");
        check(DoctorFacts.Battery(128,255) == "No system battery", "Desktop battery absence");
        check(DoctorFacts.Battery(0,255) == "Unknown" && DoctorFacts.Battery(8,255) == "Unknown", "Unknown percentage not fabricated");
        check(DoctorFacts.Battery(8,100) == "100%" && DoctorFacts.Battery(4,0) == "0%", "Battery endpoints");
        check(DoctorFacts.PowerSource(255) == "Unknown" && DoctorFacts.PowerSource(0) == "On battery" && DoctorFacts.PowerSource(1) == "Plugged in", "AC status sentinels");
        check(!DoctorFacts.LowSpace(0,0), "Missing disk capacity not diagnosed");
        check(DoctorFacts.LowSpace(4UL<<30, 20UL<<30) && DoctorFacts.LowSpace(8UL<<30, 100UL<<30) && !DoctorFacts.LowSpace(50UL<<30,100UL<<30), "Low space absolute and proportional thresholds");
        check(DoctorFacts.Visible("safe\u202e\ntext") == "safe[U+202E][U+000A]text", "Process-name control characters visible");
        check(DoctorFacts.Visible(new string('a',1000)).Length == 161, "Displayed system strings bounded");
        check(!new Prefs().IsOn(Feature.Doctor), "Doctor independently disabled by default");
    }
}
