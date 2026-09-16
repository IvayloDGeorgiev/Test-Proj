namespace Test_Proj.Export;

/// <summary>OS boundary for fault injection; safety checks remain in the exporter.</summary>
public interface IAtomicFileOperations
{
    Stream CreateTemporary(string path);
    void Publish(string temporary, string destination);
    void DeleteTemporary(string path);
}

public sealed class AtomicFileOperations : IAtomicFileOperations
{
    public Stream CreateTemporary(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
    public void Publish(string temporary, string destination)
    {
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }
    public void DeleteTemporary(string path) => File.Delete(path);
}
