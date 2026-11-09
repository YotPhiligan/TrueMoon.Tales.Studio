namespace TrueMoon.Tales.Studio.Configuration;

internal static class StudioApplicationExtensions
{
    public static async Task<int> RunStudioAsync(this IAppBuilder builder, string[] args, CancellationToken token = default)
    {
        if (!StudioOptions.TryParse(args, out var options))
        {
            Console.Error.WriteLine(StudioOptions.Usage);
            return 2;
        }

        await builder.RunAsync(app => app.Services(services => services.Instance(options)), token);
        return 0;
    }
}
