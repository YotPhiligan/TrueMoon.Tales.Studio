using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TrueMoon.Tales.Studio.Backend;

internal static partial class BackendFullTransfer
{
    private static readonly string[] PromptSections =
    [
        "subject_definitions", "summary", "retention_analysis", "detailed_description", "overall_soundscape", "non_diegetic_music"
    ];
    private static readonly JsonSerializerOptions WorkflowJson = new() { WriteIndented = true };

    private static void ValidateWorkflowPrompt(string prompt, int pictures)
    {
        if (!Regex.Matches(prompt, @"^([a-z_]+):", RegexOptions.Multiline).Select(match => match.Groups[1].Value).SequenceEqual(PromptSections) ||
            !Labels("Picture").SetEquals(Enumerable.Range(1, pictures)) || !Labels("Video").SetEquals([1]) ||
            Regex.IsMatch(prompt, @"<Audio \d+>"))
            throw new ScenarioEditException("transfer_invalid_profile");
        var sound = prompt.IndexOf("overall_soundscape:", StringComparison.Ordinal) + "overall_soundscape:".Length;
        var music = prompt.IndexOf("\n\nnon_diegetic_music:", sound, StringComparison.Ordinal);
        if (music < 0 || prompt[sound..music].Trim() != "N/A" || prompt[(music + "\n\nnon_diegetic_music:".Length)..].Trim() != "N/A")
            throw new ScenarioEditException("transfer_prompt_sound");

        HashSet<int> Labels(string type) => Regex.Matches(prompt, $@"<{type} (\d+)>")
            .Select(match => int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1)
            .ToHashSet();
    }

    private static async Task WriteWorkflowAsync(string preview, string config, byte[] configBytes, FullTransferRequest request,
        Pin[] pins, string comfy, bool hasAudio, CancellationToken token)
    {
        var worker = BackendFullTransferWorker.DirectoryPath;
        var templates = Path.Combine(worker, "Templates");
        var nativeApi = ReadFile(templates, "Native.api.json");
        var nativeUi = ReadFile(templates, "Native.ui.json");
        var outputNodes = ReadFile(templates, "OutputNodes.ui.json");
        var id = JobId(request.Form.ClientKey);
        var frames = request.Draft.Frames;
        var seconds = frames / (double)Fps;
        var inputs = pins.Select((pin, index) =>
        {
            var digest = pin.Input.Sha256.ToLowerInvariant();
            var name = $"TMT.{id}.{index + 1}.{digest[..10]}{Path.GetExtension(pin.Path).ToLowerInvariant()}";
            return new { role = pin.Input.Role, Source = pin.Path, Target = Path.Combine(comfy, "input", name), SHA256 = digest };
        }).ToArray();
        var api = JsonNode.Parse(nativeApi)!.AsObject();
        BuildApi(api, request.Form, id, frames, seconds, inputs.Select(input => Path.GetFileName(input.Target)).ToArray());
        var ui = JsonNode.Parse(nativeUi)!.AsObject();
        BuildUi(ui, JsonNode.Parse(outputNodes)!.AsArray(), api, request.Form, id, frames);

        var models = new[] {
            (Node: "127", Folder: "diffusion_models", Key: "unet_name"),
            (Node: "128", Folder: "text_encoders", Key: "clip_name"),
            (Node: "119", Folder: "vae", Key: "vae_name"),
            (Node: "120", Folder: "vae", Key: "vae_name")
        }.Select(row =>
        {
            token.ThrowIfCancellationRequested();
            var name = Parameters(api, row.Node)[row.Key]!.GetValue<string>();
            if (name != Path.GetFileName(name)) throw new ScenarioEditException("transfer_invalid_profile");
            var path = Path.Combine(comfy, "models", row.Folder, name);
            BackendMediaResolver.CheckNoLinks(path);
            var file = new FileInfo(path);
            return new { path, bytes = file.Length, mtime_ns = checked((file.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100) };
        }).ToArray();

        var implementation = Directory.EnumerateFiles(worker).Select(Path.GetFileName).OfType<string>()
            .Where(name => Path.GetExtension(name) is ".py" or ".ps1").Order(StringComparer.Ordinal)
            .ToDictionary(name => name, name => Digest(ReadFile(worker, name)), StringComparer.Ordinal);
        var artifacts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Job.json"] = configBytes,
            ["Prepared.api.json"] = Serialize(api),
            ["Workflow.ui.json"] = Serialize(ui),
            ["Inputs.prepared.json"] = Serialize(inputs),
            ["Prompt.txt"] = Encoding.UTF8.GetBytes(request.Form.Prompt.ReplaceLineEndings(Environment.NewLine))
        };
        var build = new
        {
            version = 2, id, profile = "full-transfer", config_source = config, frames, fps = Fps, size = new[] { Width, Height },
            requested_seconds = seconds, duration_seconds = seconds, silent = true,
            motion_video = new { frames, fps = Fps, size = new[] { request.Draft.Width, request.Draft.Height },
                seconds, has_audio = hasAudio, audio_connected = false },
            source_input_hashes = inputs, model_files = models, staged = false, generation_executed = false, production_accepted = false,
            file_hashes = artifacts.ToDictionary(row => row.Key, row => Digest(row.Value)), implementation_hashes = implementation,
            template_hashes = new Dictionary<string, string>
            {
                ["Native.api.json"] = Digest(nativeApi), ["Native.ui.json"] = Digest(nativeUi), ["OutputNodes.ui.json"] = Digest(outputNodes)
            },
            model_hash_scope = "Size and nanosecond mtime only; weights not rehashed",
            preparation = "studio-csharp"
        };
        token.ThrowIfCancellationRequested();
        BackendMediaResolver.CheckNoLinks(preview, allowMissing: true);
        Directory.CreateDirectory(preview);
        foreach (var (name, bytes) in artifacts)
            await WriteNewAsync(Path.Combine(preview, name), bytes, token).ConfigureAwait(false);
        await WriteNewAsync(Path.Combine(preview, "Build.json"), Serialize(build), token).ConfigureAwait(false);

        static string Digest(byte[] bytes) => Hash(bytes).ToLowerInvariant();
        static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, WorkflowJson);
    }

    private static void BuildApi(JsonObject api, FullTransferForm form, string id, int frames, double seconds, string[] inputs)
    {
        foreach (var (key, value) in api.ToArray())
            if (value!["class_type"]!.GetValue<string>() is "LoadImage" or "MiniMaxH3AddGuide" or "LoadVideo" or "GetVideoComponents")
                api.Remove(key);
        var reference = Parameters(api, "136");
        foreach (var (key, _) in reference.ToArray())
            if (key.StartsWith("ref_", StringComparison.Ordinal) && key != "ref_image_size") reference.Remove(key);
        reference["width"] = Width;
        reference["height"] = Height;
        reference["ref_image_size"] = "match";
        Parameters(api, "138")["value"] = form.Prompt;
        Parameters(api, "132")["value"] = seconds;
        Parameters(api, "129")["noise_seed"] = form.Seed;
        Parameters(api, "92")["filename_prefix"] = "video/" + id + ".Raw";
        for (var index = 0; index < form.Images.Length; index++)
        {
            var node = NodeId(1001 + index);
            api[node] = ApiNode("LoadImage", new() { ["image"] = inputs[index] });
            reference[$"ref_images.ref_image_{index}"] = Link(node);
        }
        api["170"] = ApiNode("MiniMaxH3AddGuide", new()
        {
            ["positive"] = Link("136"), ["latent"] = new JsonArray("136", 1), ["vae"] = Link("119"),
            ["audio_vae"] = Link("120"), ["image"] = Link(NodeId(1000 + form.GuidePicture)), ["frame_idx"] = 0
        });
        Parameters(api, "126")["conditioning"] = Link("170");
        api["171"] = ApiNode("LoadVideo", new() { ["file"] = inputs[^1] });
        api["172"] = ApiNode("GetVideoComponents", new() { ["video"] = Link("171") });
        reference["ref_videos.ref_video_0"] = Link("172");
        api["201"] = ApiNode("ImageFromBatch", new() { ["image"] = Link("122"), ["batch_index"] = frames - 1, ["length"] = 1 });
        api["202"] = ApiNode("SaveImage", new() { ["images"] = Link("201"), ["filename_prefix"] = "frames/" + id + ".Last" });

        static JsonObject ApiNode(string type, JsonObject parameters) => new() { ["class_type"] = type, ["inputs"] = parameters };
        static JsonArray Link(string node) => new(node, 0);
    }

    private static readonly IReadOnlyDictionary<int, (int X, int Y)> WorkflowPositions = new Dictionary<int, (int, int)>
    {
        [127] = (0,0), [128] = (0,150), [119] = (0,330), [120] = (0,440), [138] = (720,0), [132] = (0,600),
        [131] = (330,600), [143] = (0,720), [129] = (330,720), [165] = (720,700), [123] = (720,880), [124] = (1100,700),
        [126] = (1500,700), [125] = (1900,700), [122] = (2250,700), [130] = (2600,700), [92] = (2950,700),
        [136] = (720,1200), [170] = (1500,1200), [171] = (0,1700), [172] = (400,1700), [201] = (2600,1150), [202] = (2950,1150)
    };

    private static void BuildUi(JsonObject ui, JsonArray outputNodes, JsonObject api, FullTransferForm form, string id, int frames)
    {
        var definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var definition in ui["nodes"]!.AsArray().Concat(outputNodes))
            definitions[definition!["type"]!.GetValue<string>()] = definition;
        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, spec) in api)
        {
            var type = spec!["class_type"]!.GetValue<string>();
            var values = Parameters(api, key);
            var node = definitions[type].DeepClone().AsObject();
            var number = int.Parse(key, CultureInfo.InvariantCulture);
            node["id"] = number;
            node["order"] = number;
            node["mode"] = 0;
            node["flags"] = new JsonObject();
            foreach (var slot in Ports(node, "inputs")) slot!["link"] = null;
            foreach (var slot in Ports(node, "outputs")) slot!["links"] = new JsonArray();
            if (type == "MiniMaxH3ReferenceToVideo")
            {
                var ports = node["inputs"]!.AsArray();
                foreach (var port in ports.ToArray())
                {
                    var name = port!["name"]!.GetValue<string>();
                    if (name.StartsWith("ref_", StringComparison.Ordinal) && name != "ref_image_size") ports.Remove(port);
                }
                for (var index = 0; index < form.Images.Length; index++)
                    ports.Insert(3 + index, Port($"ref_images.ref_image_{index}"));
                ports.Insert(3 + form.Images.Length, Port("ref_videos.ref_video_0"));
            }
            var named = new JsonObject();
            foreach (var (name, value) in values)
                if (value is JsonValue) named.Add(name, value.DeepClone());
            if (type == "MiniMaxH3ReferenceToVideo") { named["prompt"] = ""; named["length"] = frames; }
            node["widgets_values_named"] = named;
            if (Widgets(type, values, frames) is { } widgets) node["widgets_values"] = widgets;
            node["title"] = type == "LoadImage" ? $"Picture {number - 1000} / {form.Images[number - 1001].Role}" : $"{key} / {type}";
            var row = (int)Math.Floor((number - 1001) / 4d);
            var (x, y) = WorkflowPositions.TryGetValue(number, out var position) ? position :
                ((number - 1001 - row * 4) * 450, 2150 + row * 400);
            node["pos"] = new JsonArray(x, y);
            if (number is > 1000 and < 1100) node["size"] = new JsonArray(400, 320);
            if (number == 138)
            {
                node["size"] = new JsonArray(850, 620);
                node["title"] = "PROMPT / ordered Picture roles; Video 1 only for transfer";
            }
            if (number == 92) node["size"] = new JsonArray(800, 360);
            if (number == 171) node["title"] = "Video 1 / selected motion / IMAGE batch only; audio disconnected";
            nodes.Add(key, node);
        }
        var links = new JsonArray();
        foreach (var (key, _) in api)
            foreach (var (name, value) in Parameters(api, key))
            {
                if (value is not JsonArray connection) continue;
                var source = nodes[connection[0]!.GetValue<string>()];
                var index = connection[1]!.GetValue<int>();
                var output = source["outputs"]![index]!;
                var destination = nodes[key]["inputs"]!.AsArray();
                var target = destination.Single(port => port!["name"]!.GetValue<string>() == name)!;
                var linkId = links.Count + 1;
                links.Add(new JsonArray(linkId, source["id"]!.DeepClone(), index, nodes[key]["id"]!.DeepClone(),
                    destination.IndexOf(target), output["type"]!.DeepClone()));
                output["links"]!.AsArray().Add(linkId);
                target["link"] = linkId;
            }
        ui["id"] = Guid.NewGuid().ToString();
        ui["revision"] = 0;
        ui["nodes"] = new JsonArray(nodes.Values.ToArray<JsonNode?>());
        ui["links"] = links;
        ui["groups"] = new JsonArray();
        ui["last_node_id"] = nodes.Values.Max(node => node["id"]!.GetValue<int>());
        ui["last_link_id"] = links.Count;
        ui["extra"] = new JsonObject
        {
            ["ds"] = new JsonObject { ["scale"] = .45, ["offset"] = new JsonArray(50, 50) },
            ["tmt_pipeline"] = new JsonObject { ["version"] = 2, ["profile"] = "full-transfer", ["job"] = id, ["silent"] = true }
        };
        static JsonObject Port(string name) => new() { ["name"] = name, ["type"] = "IMAGE", ["link"] = null };
        static IEnumerable<JsonNode?> Ports(JsonObject node, string name) => node[name] is JsonArray ports ? ports : [];
    }

    private static JsonArray? Widgets(string type, JsonObject values, int frames)
    {
        JsonNode? Read(string key) => values[key]?.DeepClone();
        return type switch
        {
            "VAELoader" => new(Read("vae_name")),
            "UNETLoader" => new(Read("unet_name"), Read("weight_dtype")),
            "CLIPLoader" => new(Read("clip_name"), Read("type"), Read("device")),
            "LoadImage" => new(Read("image"), "image"),
            "LoadVideo" => new(Read("file")),
            "PrimitiveStringMultiline" or "PrimitiveFloat" or "PrimitiveInt" => new(Read("value")),
            "RandomNoise" => new(Read("noise_seed"), "fixed"),
            "MiniMaxH3ReferenceToVideo" => new("", Width, Height, frames, "match"),
            "MiniMaxH3AddGuide" => new(0),
            "ImageFromBatch" => new(frames - 1, 1),
            "SaveImage" => new(Read("filename_prefix")),
            "SaveVideo" => new(Read("filename_prefix"), "auto", "auto", "auto"),
            _ => null
        };
    }

    private static JsonObject Parameters(JsonObject api, string node) => api[node]!["inputs"]!.AsObject();
    private static string NodeId(int number) => number.ToString(CultureInfo.InvariantCulture);
}
