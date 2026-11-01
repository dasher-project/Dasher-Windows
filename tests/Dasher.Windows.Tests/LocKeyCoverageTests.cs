using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// Every localisation call site must reference a key that exists in the
/// neutral resx. Loc.Tr falls back to the KEY STRING itself when a key is
/// missing — a typo ships as visible UI text ("backspace_tooltip" rendered
/// on the button). The CI resx-freshness gate keeps resx ↔ catalogue in
/// sync; this test closes the remaining gap, call sites ↔ resx.
/// Found by review loop 1 on the localisation PR (#77).
/// </summary>
public class LocKeyCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static HashSet<string> NeutralResxKeys()
    {
        var path = Path.Combine(RepoRoot(), "src", "Dasher.Windows", "Resources", "Strings.resx");
        var doc = XDocument.Load(path);
        var keys = new HashSet<string>();
        foreach (var data in doc.Descendants("data"))
        {
            var name = data.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name))
                keys.Add(name);
        }
        return keys;
    }

    [Fact]
    public void Every_loc_Tr_key_exists_in_neutral_resx()
    {
        var root = RepoRoot();
        var srcDir = Path.Combine(root, "src");
        var resxKeys = NeutralResxKeys();

        // {loc:Tr Key=x} in AXAML; Loc.Tr("x" (incl. Services.Loc.Tr) in C#
        var axaml = new Regex(@"\{loc:Tr\s+Key=([A-Za-z0-9_]+)\}");
        var cs = new Regex(@"Loc\.Tr\(\s*""([A-Za-z0-9_]+)""");

        var missing = new List<string>();
        var checkedCount = 0;

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext is not (".axaml" or ".cs"))
                continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var content = File.ReadAllText(file);
            foreach (var m in axaml.Matches(content))
                Check(((Match)m).Groups[1].Value);
            foreach (var m in cs.Matches(content))
                Check(((Match)m).Groups[1].Value);

            void Check(string key)
            {
                checkedCount++;
                if (!resxKeys.Contains(key))
                    missing.Add($"{Path.GetRelativePath(root, file)}: {key}");
            }
        }

        Assert.True(missing.Count == 0,
            $"{missing.Count} of {checkedCount} localisation keys missing from Strings.resx " +
            "(a typo'd key renders the key string itself in the UI):\n" + string.Join("\n", missing));
        Assert.True(checkedCount > 100, $"suspiciously few call sites found ({checkedCount}) - scanner broken?");
    }
}
