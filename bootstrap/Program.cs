using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    throw new PlatformNotSupportedException("Windows runner required");

var bytes = Encoding.UTF8.GetBytes("Chappy Storage Compressor - build probe");
var hash = SHA256.HashData(bytes);
if (hash.Length != 32) throw new Exception("SHA-256 failed");

var path = Path.Combine(Path.GetTempPath(), "chappy-ci-probe-" + Guid.NewGuid().ToString("N") + ".bin");
try
{
    File.WriteAllBytes(path, bytes);
    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        throw new Exception("Temporary-file round trip failed");
    Console.WriteLine("BOOTSTRAP PASS: Windows/.NET build, SHA-256 and TEMP I/O");
}
finally
{
    if (File.Exists(path)) File.Delete(path);
}
