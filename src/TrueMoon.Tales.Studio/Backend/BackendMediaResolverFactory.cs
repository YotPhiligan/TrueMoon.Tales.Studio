namespace TrueMoon.Tales.Studio.Backend;

// Deferred creation keeps metadata editing available when media configuration is incomplete.
internal sealed class BackendMediaResolverFactory(BackendOptions options) : IFactory<BackendMediaResolver>
{
    public BackendMediaResolver Create() => new(options);
    public BackendMediaResolver Create<TData>(TData? data = default) => data is null ? Create()
        : throw new ArgumentException("Media resolver does not accept runtime paths.", nameof(data));
}
