using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ZipToJar;

internal sealed record Jdk(string Home, int Major);

internal static class JavaFinder
{
    private const int MinMajor = 21;

    /// <summary>Шукає встановлений JDK (з javac.exe) версії 21+. Для Gradle потрібен саме JDK, не JRE.</summary>
    public static Jdk Find(Action<string> log)
    {
        var homes = new List<string>();

        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p.Trim().Trim('"'));
                if (File.Exists(Path.Combine(full, "bin", "javac.exe")) &&
                    !homes.Contains(full, StringComparer.OrdinalIgnoreCase))
                    homes.Add(full);
            }
            catch
            {
                // ігноруємо кривий шлях
            }
        }

        // 1) JAVA_HOME
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));

        // 2) PATH (…\bin -> батьківська папка = JAVA_HOME)
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { Add(Directory.GetParent(dir.Trim().Trim('"'))?.FullName); }
            catch { }
        }

        // 3) типові місця встановлення
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new[]
        {
            Path.Combine(pf, "Java"),
            Path.Combine(pf, "Eclipse Adoptium"),
            Path.Combine(pf, "Microsoft"),
            Path.Combine(pf, "Zulu"),
            Path.Combine(pf, "Amazon Corretto"),
            Path.Combine(pf, "BellSoft"),
            Path.Combine(pf, "Semeru"),
            Path.Combine(pf, "AdoptOpenJDK"),
            Path.Combine(pf, "RedHat"),
            Path.Combine(pf, "Android", "Android Studio", "jbr"),
            Path.Combine(user, ".jdks"),
            Path.Combine(user, ".gradle", "jdks"),
            Path.Combine(local, "Programs", "Eclipse Adoptium")
        };
        foreach (var r in roots) Scan(r, 2, Add);

        var found = homes
            .Select(h => new Jdk(h, GetMajor(h)))
            .Where(j => j.Major > 0)
            .ToList();

        foreach (var j in found)
            log("   знайшов JDK " + j.Major + ": " + j.Home);

        // беремо найменшу версію >= 21 (тобто 21, якщо є) — з нею Gradle/Loom дружать найкраще
        var best = found.Where(j => j.Major >= MinMajor).OrderBy(j => j.Major).FirstOrDefault();
        if (best is null)
        {
            var extra = found.Count > 0
                ? " Знайдено лише старіші версії: " + string.Join(", ", found.Select(j => j.Major)) + "."
                : "";
            throw new InvalidOperationException(
                "Не знайшов JDK 21." + extra +
                " Встанови Temurin JDK 21 (https://adoptium.net/temurin/releases/?version=21&os=windows) " +
                "і запусти знову. Потрібен саме JDK (з javac), а не JRE із лаунчера Minecraft.");
        }
        return best;
    }

    private static void Scan(string root, int depth, Action<string> add)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            add(root);
            if (depth == 0) return;
            foreach (var d in Directory.GetDirectories(root))
                Scan(d, depth - 1, add);
        }
        catch
        {
            // немає доступу — пропускаємо
        }
    }

    private static int GetMajor(string home)
    {
        try
        {
            // швидкий спосіб: файл "release" у корені JDK
            var rel = Path.Combine(home, "release");
            if (File.Exists(rel))
            {
                var m = Regex.Match(File.ReadAllText(rel), @"JAVA_VERSION=""?(\d+)(?:\.(\d+))?");
                var major = ParseMajor(m);
                if (major > 0) return major;
            }

            // запасний: java -version
            var psi = new ProcessStartInfo(Path.Combine(home, "bin", "java.exe"), "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return 0;
            var text = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return ParseMajor(Regex.Match(text, @"version ""(\d+)(?:\.(\d+))?"));
        }
        catch
        {
            return 0;
        }
    }

    private static int ParseMajor(Match m)
    {
        if (!m.Success) return 0;
        var a = int.Parse(m.Groups[1].Value);
        // старий формат: 1.8.0 -> 8
        if (a == 1 && m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
        return a;
    }
}
