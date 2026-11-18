using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ZipToJar;

internal static class Converter
{
    private static Process? _current;

    private static readonly string[] GradleMarkers = { "build.gradle", "build.gradle.kts" };

    private static readonly string[] ModMarkers =
    {
        "fabric.mod.json",
        "quilt.mod.json",
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.toml"
    };

    public static void KillCurrent()
    {
        try { _current?.Kill(true); } catch { }
    }

    /// <summary>Головна точка: zip -> один або кілька .jar поруч із zip.</summary>
    public static List<string> Run(string zipPath, Action<string> log)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Файл не знайдено: " + zipPath);

        var work = MakeWorkDir();
        try
        {
            log("== " + Path.GetFileName(zipPath) + " ==");
            log("» Розпаковую архів...");
            var src = Path.Combine(work, "src");
            ZipFile.ExtractToDirectory(zipPath, src, true);

            var outDir = GetOutputDir(zipPath);

            // 1) Проєкт із Gradle -> справжня компіляція
            var gradleRoot = FindRoot(src, GradleMarkers);
            if (gradleRoot != null)
                return BuildWithGradle(gradleRoot, outDir, log);

            // 2) Готова папка мода (fabric.mod.json + класи/ресурси) -> просто пакуємо в .jar
            var modRoot = FindRoot(src, ModMarkers);
            if (modRoot != null)
            {
                var jar = Pack(modRoot, outDir, Path.GetFileNameWithoutExtension(zipPath), log);
                return new List<string> { jar };
            }

            throw new InvalidOperationException(
                "Це не схоже на мод: в архіві немає ні build.gradle, ні fabric.mod.json.");
        }
        finally
        {
            TryDelete(work);
        }
    }

    // ------------------------------------------------------------ Gradle

    private static List<string> BuildWithGradle(string root, string outDir, Action<string> log)
    {
        log("» Знайдено Gradle-проєкт: " + Path.GetFileName(root));
        log("» Шукаю JDK 21...");
        var jdk = JavaFinder.Find(log);
        log("   Використовую JDK " + jdk.Major + ": " + jdk.Home);

        var wrapperJar = Path.Combine(root, "gradle", "wrapper", "gradle-wrapper.jar");
        var wrapperBat = Path.Combine(root, "gradlew.bat");

        ProcessStartInfo psi;
        if (File.Exists(wrapperJar))
        {
            // Do NOT execute gradlew.bat through ProcessStartInfo: Windows batch
            // argument quoting can collapse the entire Gradle command into one task.
            // Launch the wrapper's Java entry point and add every argument separately.
            var javaExe = Path.Combine(jdk.Home, "bin", "java.exe");
            if (!File.Exists(javaExe))
                throw new InvalidOperationException("Не знайдено java.exe у вибраному JDK: " + javaExe);

            psi = new ProcessStartInfo(javaExe)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-classpath");
            psi.ArgumentList.Add(wrapperJar);
            psi.ArgumentList.Add("org.gradle.wrapper.GradleWrapperMain");
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("--no-daemon");
            psi.ArgumentList.Add("--console=plain");
            log("   Використовую Gradle Wrapper напряму через Java (аргументи передаються окремо).");
        }
        else
        {
            var systemGradle = FindOnPath("gradle.bat") ?? FindOnPath("gradle.cmd");
            if (systemGradle == null)
                throw new InvalidOperationException(
                    "У ZIP немає gradle/wrapper/gradle-wrapper.jar, а Gradle в системі не встановлений. " +
                    "Додай у ZIP gradle/wrapper/gradle-wrapper.jar або встанови Gradle.");

            // System Gradle is a batch file, so invoke it through cmd.exe.
            psi = new ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"\"\"{systemGradle}\" build --no-daemon --console=plain\"");
            log("   Немає Gradle Wrapper — використовую системний Gradle.");
        }

        log("» Збираю мод через Gradle (перший раз довго: тягнуться залежності з інтернету)...");

        psi.Environment["JAVA_HOME"] = jdk.Home;
        psi.Environment["PATH"] = Path.Combine(jdk.Home, "bin") + ";" +
                                  (Environment.GetEnvironmentVariable("PATH") ?? "");

        using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };

        proc.Start();
        _current = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        // Ensure asynchronous stdout/stderr event handlers have drained.
        proc.WaitForExit();
        _current = null;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Gradle завершився з помилкою (код {proc.ExitCode}). " +
                "Помилка вище в логу — її можна скопіювати кнопкою внизу.");

        var libs = Path.Combine(root, "build", "libs");
        var jars = Directory.Exists(libs)
            ? Directory.GetFiles(libs, "*.jar")
                .Where(f => !Regex.IsMatch(
                    Path.GetFileName(f),
                    @"-(sources|javadoc|dev|dev-shadow)\.jar$",
                    RegexOptions.IgnoreCase))
                .ToList()
            : new List<string>();

        if (jars.Count == 0)
            throw new InvalidOperationException("Gradle відпрацював, але в build/libs немає .jar.");

        var result = new List<string>();
        foreach (var jar in jars)
        {
            var dest = Path.Combine(outDir, Path.GetFileName(jar));
            File.Copy(jar, dest, true);
            result.Add(dest);
        }
        return result;
    }

    // ------------------------------------------------- пакування папки в .jar

    private static string Pack(string root, string outDir, string baseName, Action<string> log)
    {
        var hasClasses = Directory.EnumerateFiles(root, "*.class", SearchOption.AllDirectories).Any();
        var hasJava = Directory.EnumerateFiles(root, "*.java", SearchOption.AllDirectories).Any();

        if (!hasClasses && hasJava)
            throw new InvalidOperationException(
                "В архіві лише вихідний код (.java) без build.gradle — його не можна скомпілювати. " +
                "Додай у ZIP весь проєкт разом з build.gradle і gradlew.bat.");

        log("» Пакую папку мода в .jar...");

        var jarPath = Path.Combine(outDir, SafeName(baseName) + ".jar");
        if (File.Exists(jarPath)) File.Delete(jarPath);

        var count = 0;
        using (var fs = new FileStream(jarPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            if (!File.Exists(Path.Combine(root, "META-INF", "MANIFEST.MF")))
            {
                var manifest = zip.CreateEntry("META-INF/MANIFEST.MF");
                using var w = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                w.Write("Manifest-Version: 1.0\r\n\r\n");
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsJunk(rel)) continue;
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                count++;
            }
        }

        log("   Файлів у .jar: " + count);
        return jarPath;
    }

    private static bool IsJunk(string rel) =>
        rel.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        rel.EndsWith(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        rel.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        rel.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase);

    // ----------------------------------------------------------- допоміжне

    /// <summary>Шукає в ширину першу папку, де є хоч один із маркерних файлів.</summary>
    private static string? FindRoot(string start, string[] markers, int maxDepth = 4)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            if (markers.Any(m => File.Exists(Path.Combine(dir, m))))
                return dir;

            if (depth >= maxDepth) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.'))
                    continue;
                queue.Enqueue((s, depth + 1));
            }
        }
        return null;
    }

    /// <summary>
    /// Коротка робоча папка (C:\ZipToJar_work\...), щоб Gradle не впирався в ліміт 260 символів шляху.
    /// </summary>
    private static string MakeWorkDir()
    {
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var candidates = new[]
        {
            Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "ZipToJar_work", id),
            Path.Combine(Path.GetTempPath(), "ZipToJar", id)
        };

        foreach (var c in candidates)
        {
            try
            {
                Directory.CreateDirectory(c);
                return c;
            }
            catch
            {
                // пробуємо наступний варіант
            }
        }
        throw new IOException("Не вдалося створити тимчасову папку.");
    }

    /// <summary>.jar кладемо поруч із zip; якщо туди писати не можна — на робочий стіл.</summary>
    private static string GetOutputDir(string zipPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        try
        {
            var probe = Path.Combine(dir, ".ziptojar_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "mod" : name;
    }

    private static string? FindOnPath(string file)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), file);
                if (File.Exists(p)) return p;
            }
            catch
            {
                // некоректний шлях у PATH
            }
        }
        return null;
    }

    private static void TryDelete(string dir)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(dir, true);
                }
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }
}
