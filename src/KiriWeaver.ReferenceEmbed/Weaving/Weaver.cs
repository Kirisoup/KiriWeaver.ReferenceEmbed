using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Build.Framework;
using Mono.Cecil;

namespace KiriWeaver.ReferenceEmbed.Weaving;

public class Weaver : Microsoft.Build.Utilities.Task
{
	[Required]
	public required string InputAssembly { get; set; }

	[Required]
	public required string OutputAssembly { get; set; }

    [Required]
    public required ITaskItem[] ReferencePaths { get; set; }
	
	private enum AttrType : byte {
		Config,
		IncludeAll,
		Include,
		Exclude,
	}

	private readonly record struct AttrInfo(
		AttrType Type,
		IEnumerable<object> Args);

	private readonly record struct EmbedInfo(
		string Prefix,
		HashSet<string> Filter,
		bool ExcludeMode,
		bool DefaultCompression,
		Dictionary<string, bool> CompressionMap);

	public override bool Execute() {
		try {
            using var assembly = AssemblyDefinition.ReadAssembly(InputAssembly);

			var info = GetEmbedInfo(assembly);

			var resources = ReferencePaths
				.Select(r => r.ItemSpec)
				.Where(file => info.ExcludeMode ^ info.Filter.Remove(
					Path.GetFileNameWithoutExtension(file)))
				.Select(file => IntoResource(file, in info));

			foreach (var resource in resources) 
				assembly.MainModule.Resources.Add(resource);

			assembly.Write(OutputAssembly);
			return true;
		}
		catch (Exception ex) {
			Log.LogError($"""
				{nameof(ReferenceEmbed)} weaving failed because {ex.Message}
				StackTrace: 
				{ex.StackTrace}
				""");
			return false;
		}
	}

	private static EmbedInfo GetEmbedInfo(AssemblyDefinition assembly) 
	{
		const string @namespace = $"{nameof(KiriWeaver)}.{nameof(ReferenceEmbed)}";
		var Prefix = nameof(ReferenceEmbed);
		HashSet<string> Filter = [];
		bool? ExcludeMode = null;
		var DefaultCompression = false;
		Dictionary<string, bool> CompressionMap = [];
		List<int> removeIdx = [];

		for (int i = 0; i < assembly.CustomAttributes.Count; i++) {
			var attr = assembly.CustomAttributes[i];
			switch (attr.AttributeType.FullName) {
				case $"{@namespace}.{nameof(EmbedConfigAttribute)}": {
					var args = attr.ConstructorArguments.Select(arg => arg.Value);
					if (args.FirstOrDefault(arg => arg is bool) is true)
						DefaultCompression = true;
					if (args.FirstOrDefault(arg => arg is string) is string prefix)
						Prefix = prefix;
					break;
				}
				case $"{@namespace}.{nameof(EmbedIncludeAllAttribute)}": 
					ExcludeMode ??= true;
					break;
				case $"{@namespace}.{nameof(EmbedIncludeAttribute)}": {
					var args = attr.ConstructorArguments.Select(arg => arg.Value);
					if (args.FirstOrDefault(arg => arg is string) is not string name) break;
					ExcludeMode ??= false;
					if (!ExcludeMode.Value) {
						Filter.Add(name);
					} else {
						Filter.Remove(name);
					}
					if (args.FirstOrDefault(arg => arg is bool) is not bool compress) break;
					CompressionMap.Add(name, compress);
					break;
				}
				case $"{@namespace}.{nameof(EmbedExcludeAttribute)}": {
					var args = attr.ConstructorArguments.Select(arg => arg.Value);
					if (args.FirstOrDefault() is not string file) break;
					ExcludeMode ??= true;
					var name = Path.GetFileNameWithoutExtension(file);
					if (ExcludeMode.Value) {
						Filter.Add(name);
					} else {
						Filter.Remove(name);
					}
					break;
				}
			}
		}

		// remove attributes in descending order, avoiding messing up the index 
		for (int i = removeIdx.Count - 1; i >= 0; i--)
			assembly.CustomAttributes.RemoveAt(removeIdx[i]);

		var reference = assembly.MainModule.AssemblyReferences
			.FirstOrDefault(r => r.Name == Assembly.GetExecutingAssembly().GetName().Name);
		if (reference is not null) 
			assembly.MainModule.AssemblyReferences.Remove(reference);

		return new(Prefix + '.', Filter, ExcludeMode ?? false, DefaultCompression, CompressionMap);
	}

	private static EmbeddedResource IntoResource(string path, in EmbedInfo info) {
		byte[] fileBytes = File.ReadAllBytes(path);
		
		var fileName = Path.GetFileNameWithoutExtension(path);
		var embedName = info.Prefix + fileName;

		bool compress = info.CompressionMap.TryGetValue(fileName, out var value)
			? value
			: info.DefaultCompression;

		if (compress) {
			var stream = new MemoryStream();
			using (var compressStream = new DeflateStream(stream, CompressionLevel.Optimal, true)) {
				compressStream.Write(fileBytes, 0, fileBytes.Length);
			}
			stream.Position = 0;
			return new EmbeddedResource(
				embedName + ".compressed",
				ManifestResourceAttributes.Public,
				stream);
		}
		return new(
			embedName,
			ManifestResourceAttributes.Public, 
			fileBytes);
	}
}