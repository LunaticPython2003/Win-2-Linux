namespace Win2Linux.Core.Boot;

/// <summary>Temporary files on the ESP, separate from every final boot path.</summary>
public sealed class EspStagingLayout
{
    public string Root { get; }
    public string BootDirectory => Path.Combine(Root, "boot");
    public string EfiDirectory => Path.Combine(Root, "efi");
    public string UnattendedDirectory => Path.Combine(Root, "unattended");

    public EspStagingLayout(string espRoot)
    {
        Root = Path.Combine(Path.GetFullPath(espRoot), "win2linux", "staging-" + Guid.NewGuid().ToString("N"));
    }

    public void Cleanup()
    {
        // Root is constructed here and cannot be supplied by a caller. Only this
        // staging tree is disposable; win2linux/boot and unattended are final files.
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
