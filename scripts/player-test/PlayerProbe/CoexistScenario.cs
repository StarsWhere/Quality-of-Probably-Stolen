using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Exercises only native loader decisions in the isolated QA Player.
public static class CoexistScenario
{
    [Serializable] private sealed class Profile { public string mode = null; public Package[] packages = null; }
    [Serializable] private sealed class Package { public string folder = null; public string workshopId = null; }

    private static string Cell(object value) => (value?.ToString() ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static string Row(params object[] values) => string.Join("\t", values.Select(Cell));
    private static string Id(LoadedMod mod) => mod?.Manifest?.ID ?? "";
    private static string Fingerprint(LoadedMod mod) => mod?.Fingerprint?.Combined ?? "";
    private static string RunId(string root) => File.ReadAllText(Path.Combine(root, "qa-run-id.txt")).Trim();

    public static string Run(string phase)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string data = Application.persistentDataPath;
        var profile = JsonUtility.FromJson<Profile>(File.ReadAllText(Path.Combine(root, "qa-packages.json")));
        if (profile?.mode != "coexist" || profile.packages == null || profile.packages.Length < 2)
            throw new InvalidOperationException("Invalid coexistence profile");
        string modRoot = Path.Combine(root, "Mods");
        var entries = new List<(Package package, LoadedMod mod, string path, string parseIssue)>();
        foreach (var package in profile.packages)
        {
            if (package == null || string.IsNullOrEmpty(package.folder) || Path.GetFileName(package.folder) != package.folder)
                throw new InvalidOperationException("Invalid package folder");
            string path = Path.GetFullPath(Path.Combine(modRoot, package.folder));
            LoadedMod mod = ModLoader.Mods.FirstOrDefault(m => m != null &&
                string.Equals(Path.GetFullPath(m.FolderPath), path, StringComparison.OrdinalIgnoreCase));
            string issue = "";
            if (mod == null)
            {
                var parsed = ModLoader.ParseManifestForTools(path, out issue);
                if (parsed != null && string.IsNullOrEmpty(issue)) issue = "Native loader did not register a parsed package";
                if (parsed == null && string.IsNullOrEmpty(issue)) issue = "Native manifest parser rejected the package";
            }
            entries.Add((package, mod, path, issue));
        }

        if (phase == "coexist-reset")
        {
            var rows = new List<string> { Row("folder", "id", "enabled", "running", "loadError", "trust", "parseIssue") };
            foreach (var entry in entries)
            {
                var mod = entry.mod;
                rows.Add(Row(entry.package.folder, Id(mod), mod?.IsEnabled ?? false, mod?.Instance != null,
                    mod?.LoadError, mod?.TrustState, entry.parseIssue));
                if (mod?.Manifest != null && ModLoader.IsModEnabled(mod.Manifest.ID))
                {
                    ModLoader.SetModEnabled(mod.Manifest.ID, false);
                    if (ModLoader.IsModEnabled(mod.Manifest.ID)) throw new InvalidOperationException("Unable to disable " + mod.Manifest.ID);
                }
            }
            File.WriteAllLines(Path.Combine(data, "qa-coexist-reset-" + RunId(root) + ".tsv"), rows);
            return "coexistence reset; all selected registered packages disabled for fresh native approval";
        }

        if (phase == "coexist-approve")
        {
            var rows = new List<string> { Row("folder", "workshopId", "id", "version", "decision", "fingerprint",
                "methods", "declared", "detected", "hardFlags", "undeclared", "scanError", "refusal", "needsPrompt") };
            bool shopApproved = false;
            var errors = new List<string>();
            foreach (var entry in entries)
            {
                LoadedMod mod = entry.mod;
                ModEnableCheck check = null;
                string refusal = entry.parseIssue;
                string decision = "refused";
                try
                {
                    if (mod != null)
                    {
                        if (ModLoader.IsModEnabled(Id(mod))) throw new InvalidOperationException("Package was not reset");
                        check = ModLoader.CheckEnable(mod);
                        refusal = check?.Refusal;
                        if (check == null) refusal = "Native CheckEnable returned null";
                        else if (check.Scan == null) refusal = refusal ?? "Native scan unavailable";
                        else if (check.Scan.Refused) refusal = refusal ?? "Native scan refused";
                        else if (check.Fingerprint == null) refusal = refusal ?? "Native fingerprint unavailable";
                        if (string.IsNullOrEmpty(refusal))
                        {
                            ModLoader.ApproveAndEnable(mod, check);
                            if (!ModLoader.IsModEnabled(Id(mod))) throw new InvalidOperationException("Native approval did not enable package");
                            decision = "approved";
                            if (Id(mod) == "starswhere.shopassist") shopApproved = true;
                        }
                    }
                }
                catch (Exception error) { refusal = error.ToString(); decision = "error"; errors.Add(entry.package.folder + ": " + error.Message); }
                rows.Add(Row(entry.package.folder, entry.package.workshopId, Id(mod), mod?.Manifest?.ModVersion, decision,
                    check?.Fingerprint?.Combined, check?.Scan?.MethodsScanned,
                    mod?.Manifest?.Capabilities == null ? "" : string.Join(",", mod.Manifest.Capabilities),
                    check?.Scan?.Capabilities == null ? "" : string.Join(",", check.Scan.Capabilities.OrderBy(x => x)),
                    check?.Scan?.HardFlags?.Count, check?.Undeclared == null ? "" : string.Join(",", check.Undeclared),
                    check?.Scan?.Error, refusal, check?.NeedsPrompt));
            }
            File.WriteAllLines(Path.Combine(data, "qa-coexist-approval.tsv"), rows);
            File.WriteAllLines(Path.Combine(data, "qa-coexist-approval-" + RunId(root) + ".tsv"), rows);
            if (errors.Count != 0) throw new InvalidOperationException("Native approval errors: " + string.Join("; ", errors));
            if (!shopApproved) throw new InvalidOperationException("ShopAssist was not approved; see coexistence approval report");
            return "native checks and approvals recorded for " + entries.Count + " selected packages";
        }

        if (phase == "coexist-verify")
        {
            string[] approvalRows = File.ReadAllLines(Path.Combine(data, "qa-coexist-approval.tsv"));
            var decisions = approvalRows.Skip(1).Select(row => row.Split('\t')).ToDictionary(row => row[0], row => row);
            var rows = new List<string> { Row("folder", "workshopId", "id", "version", "decision", "enabled", "running",
                "instance", "dll", "assemblyPath", "loadError", "source", "trust", "fingerprint", "note", "parseIssue") };
            var errors = new List<string>();
            foreach (var entry in entries)
            {
                var mod = entry.mod;
                if (!decisions.TryGetValue(entry.package.folder, out var decision) || decision.Length < 6)
                    throw new InvalidOperationException("Missing native approval record for " + entry.package.folder);
                bool approved = decision[4] == "approved";
                bool running = mod != null && mod.IsEnabled && mod.Instance != null && mod.Assembly != null && mod.LoadError == null;
                rows.Add(Row(entry.package.folder, entry.package.workshopId, Id(mod), mod?.Manifest?.ModVersion, decision[4],
                    mod?.IsEnabled ?? false, running, mod?.Instance?.GetType().FullName,
                    mod?.DllPath == null ? "" : Path.GetFileName(mod.DllPath), mod?.Assembly?.Location,
                    mod?.LoadError, mod?.Source, mod?.TrustState, Fingerprint(mod), mod?.TrustNote, entry.parseIssue));
                if (approved && (!running || Id(mod) != decision[2] || Fingerprint(mod) != decision[5]))
                    errors.Add(entry.package.folder + " did not reload its approved fingerprint as a running instance");
                if (!approved && mod != null && (mod.IsEnabled || mod.Instance != null))
                    errors.Add(entry.package.folder + " ran despite not being approved");
            }
            File.WriteAllLines(Path.Combine(data, "qa-coexist-runtime-" + RunId(root) + ".tsv"), rows);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
            return "coexistence runtime, trust, fingerprints, instances and load errors recorded for " + entries.Count + " selected packages";
        }
        throw new InvalidOperationException("Unknown coexistence phase " + phase);
    }
}
