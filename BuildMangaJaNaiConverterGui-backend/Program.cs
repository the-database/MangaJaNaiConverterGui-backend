// See https://aka.ms/new-console-template for more information
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using System.Diagnostics;
using System.Text;
using static Downloader;
// TODO install numpy, onnx

if (args.Length < 1)
{
    throw new ArgumentException("Version is required.");
}

// Get the path of the currently executing assembly
var assemblyDirectory = AppContext.BaseDirectory;
var backendDirectory = Path.Combine(assemblyDirectory, "backend");
var installDirectory = Path.Combine(assemblyDirectory, $"backend-v{args[0]}");
var pythonDirectory = Path.Combine(backendDirectory, "python");
var pythonPath = Path.Combine(pythonDirectory, "python.exe");

void ExtractTgz(string gzArchiveName, string destFolder)
{
    Stream inStream = File.OpenRead(gzArchiveName);
    Stream gzipStream = new GZipInputStream(inStream);

    TarArchive tarArchive = TarArchive.CreateInputTarArchive(gzipStream, Encoding.UTF8);
    tarArchive.ExtractContents(destFolder);
    tarArchive.Close();

    gzipStream.Close();
    inStream.Close();
}

async Task InstallPython()
{
    // Download Python Installer
    var downloadUrl = "https://github.com/astral-sh/python-build-standalone/releases/download/20250205/cpython-3.12.9+20250205-x86_64-pc-windows-msvc-shared-install_only.tar.gz";
    var targetPath = Path.GetFullPath("python.tar.gz");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading Python ({progress}%)...");
    });

    // Install Python 
    Console.WriteLine("Extracting Python...");
    ExtractTgz(targetPath, backendDirectory);

    File.Delete(targetPath);
}

void AddPythonPth()
{
    string[] lines = { "python312.zip", "DLLs", "Lib", ".", "Lib/site-packages" };
    var filename = "python312._pth";

    using var outputFile = new StreamWriter(Path.Combine(pythonDirectory, filename));

    foreach (string line in lines)
        outputFile.WriteLine(line);
}

async Task InstallPythonDependencies()
{
    var tomlUrl = "https://raw.githubusercontent.com/the-database/MangaJaNaiConverterGui/refs/heads/main/MangaJaNaiConverterGui/backend/src/pyproject.toml";
    var targetPath = Path.GetFullPath("pyproject.toml");

    await DownloadFileAsync(tomlUrl, targetPath, (progress) => { });

    var cmd = $@"{pythonPath} -m pip install -U pip wheel --no-warn-script-location && {pythonPath} -m pip install torch==2.7.0 torchvision --index-url https://download.pytorch.org/whl/cu128 --no-warn-script-location && {pythonPath} -m pip install ""{Path.GetFullPath(@".")}"" --no-warn-script-location";

    await RunInstallCommand(cmd);

    File.Delete(targetPath);
}

async Task InstallPythonVapourSynthPlugins()
{
    string[] dependencies = { "ffms2" };

    var cmd = $@".\python.exe vsrepo.py -p update && .\python.exe vsrepo.py -p install {string.Join(" ", dependencies)}";

    await RunInstallCommand(cmd);
}


void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged)
{

    using (var fsInput = File.OpenRead(archivePath))
    using (var zf = new ZipFile(fsInput))
    {

        for (var i = 0; i < zf.Count; i++)
        {
            ZipEntry zipEntry = zf[i];

            if (!zipEntry.IsFile)
            {
                // Ignore directories
                continue;
            }
            String entryFileName = zipEntry.Name;
            // to remove the folder from the entry:
            //entryFileName = Path.GetFileName(entryFileName);
            // Optionally match entrynames against a selection list here
            // to skip as desired.
            // The unpacked length is available in the zipEntry.Size property.

            // Manipulate the output filename here as desired.
            var fullZipToPath = Path.Combine(outFolder, entryFileName);
            var directoryName = Path.GetDirectoryName(fullZipToPath);
            if (directoryName?.Length > 0)
            {
                Directory.CreateDirectory(directoryName);
            }

            // 4K is optimum
            var buffer = new byte[4096];

            // Unzip file in buffered chunks. This is just as fast as unpacking
            // to a buffer the full size of the file, but does not waste memory.
            // The "using" will close the stream even if an exception occurs.
            using (var zipStream = zf.GetInputStream(zipEntry))
            using (Stream fsOutput = File.Create(fullZipToPath))
            {
                StreamUtils.Copy(zipStream, fsOutput, buffer);
            }

            var percentage = Math.Round((double)i / zf.Count * 100, 0);
            progressChanged?.Invoke(percentage);
        }
    }
}

async Task<string[]> RunInstallCommand(string cmd)
{
    Debug.WriteLine(cmd);

    // Create a new process to run the CMD command
    using (var process = new Process())
    {
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = @$"/C {cmd}";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.WorkingDirectory = installDirectory;

        var result = string.Empty;

        // Create a StreamWriter to write the output to a log file
        try
        {
            //using var outputFile = new StreamWriter("error.log", append: true);
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine(e.Data);
                }
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    result = e.Data;
                    Console.WriteLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine(); // Start asynchronous reading of the output
            await process.WaitForExitAsync();
        }
        catch (IOException) { }
    }

    return [];
}

void CopyDirectory(string srcDir, string targetDir)
{
    Directory.CreateDirectory(targetDir);

    foreach (string file in Directory.GetFiles(srcDir))
    {
        string targetFilePath = Path.Combine(targetDir, Path.GetFileName(file));
        File.Copy(file, targetFilePath, true); // true to overwrite existing files
    }

    foreach (string subDir in Directory.GetDirectories(srcDir))
    {
        string newTargetDir = Path.Combine(targetDir, Path.GetFileName(subDir));
        CopyDirectory(subDir, newTargetDir);
    }
}

async Task Main()
{
    if (Directory.Exists(installDirectory))
    {
        Directory.Delete(installDirectory, true);
    }
    Directory.CreateDirectory(installDirectory);
    await InstallPython();
    AddPythonPth();
    await InstallPythonDependencies();
}

await Main();