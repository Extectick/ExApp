namespace MyApp.Packaging;

public sealed class PackageException : Exception
{
    public PackageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PackageException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
