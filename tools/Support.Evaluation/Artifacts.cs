using System.Text;

namespace eShop.SupportEvaluation;

public static class Artifacts
{
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void Append<T>(string path, T value) => Write(path, EvaluationJson.Serialize(value) + "\n", append: true);
    public static void Json<T>(string path, T value) => Write(path, EvaluationJson.Serialize(value) + "\n");

    public static void Write(string path, string text, bool append = false)
    {
        using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush();
    }

    public static void PrivateJson<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Json(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
