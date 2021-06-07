using System;
using System.IO;
using System.Net.Http;
using System.Text;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;

[UnsetVisualStudioEnvironmentVariables]
public class Packager : NukeBuild
{
    public static int Main() => Execute<Packager>(x => x.All);

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath PackingDirectory => RootDirectory / "packager/native/pack";
    AbsolutePath DownloadDirectory => RootDirectory / "download";

    string VipsVersion => Environment.GetEnvironmentVariable("VIPS_VERSION") ?? "8.10.6";

    string[] NuGetArchitectures => new[]
    {
        "win-x64",
        "win-x86",
        "linux-x64",
        "linux-musl-x64",
        "linux-musl-arm64",
        "linux-arm",
        "linux-arm64",
        "osx-x64",
        "osx-arm64"
    };

    Target Clean => _ => _
        .Executes(() =>
        {
            EnsureCleanDirectory(PackingDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target DownloadBinaries => _ => _
        .DependsOn(Clean)
        .Executes(async () =>
        {
            var client = new HttpClient();

            foreach (var architecture in NuGetArchitectures)
            {
                var fileName = $"libvips-{VipsVersion}-{architecture}.tar.gz";
                var tarball =
                    new Uri(
                        $"https://github.com/EYHN/libvips-artifacts/releases/download/v{VipsVersion}/{fileName}");

                var filePath = DownloadDirectory / fileName;
                if (!File.Exists(filePath))
                {
                    Logger.Info(filePath + " not in download directory. Downloading now ...");
                    EnsureExistingDirectory(DownloadDirectory);
                    var response = await client.GetAsync(tarball);
                    await using var fs = new FileStream(filePath, FileMode.CreateNew);
                    await response.Content.CopyToAsync(fs);
                }

                var tempDir = PackingDirectory / "temp";

                Logger.Info($"Uncompressing {fileName} ...");
                await using (var inStream = File.OpenRead(filePath))
                await using (var gzipStream = new GZipInputStream(inStream))
                using (var tarArchive = TarArchive.CreateInputTarArchive(gzipStream, Encoding.UTF8))
                {
                    tarArchive.ExtractContents(tempDir);
                }

                var dllPackDir = PackingDirectory / architecture;
                EnsureExistingDirectory(dllPackDir);

                // The C++ binding isn't needed.
                tempDir.GlobFiles("lib/libvips-cpp*").ForEach(DeleteFile);

                tempDir.GlobFiles("lib/*.dll", "lib/*.so*", "lib/*.dylib", "THIRD-PARTY-NOTICES.md", "versions.json")
                    .ForEach(f => CopyFileToDirectory(f, dllPackDir));

                DeleteDirectory(tempDir);
            }
        });

    Target CreateNativeNuGetPackages => _ => _
        .DependsOn(DownloadBinaries)
        .DependsOn(Clean)
        .Executes(() =>
        {
            // Build the architecture specific packages
            NuGetPack(c => c
                .SetVersion(VipsVersion)
                .SetOutputDirectory(ArtifactsDirectory)
                .AddProperty("NoWarn", "NU5128")
                .CombineWith(NuGetArchitectures,
                    (_, architecture) =>
                        _.SetTargetPath(RootDirectory / "packager/native/LibVips.Native." + architecture + ".nuspec")));

            // Build the all-in-one package, which depends on the previous packages.
            NuGetPack(c => c
                .SetTargetPath(RootDirectory / "packager/native/LibVips.Native.nuspec")
                .SetVersion(VipsVersion)
                .SetOutputDirectory(ArtifactsDirectory)
                .AddProperty("NoWarn", "NU5128"));
        });

    Target All => _ => _
        .DependsOn(CreateNativeNuGetPackages);
}