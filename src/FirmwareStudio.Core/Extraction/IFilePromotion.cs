namespace FirmwareStudio.Core.Extraction;

internal interface IFilePromotion
{
    bool Exists(string path);
    void Move(string source, string destination, bool overwrite = false);
    void Delete(string path);
}

internal class FilePromotion : IFilePromotion
{
    public virtual bool Exists(string path) => File.Exists(path);
    public virtual void Move(string source, string destination, bool overwrite = false)
        => File.Move(source, destination, overwrite);
    public virtual void Delete(string path) => File.Delete(path);
}
