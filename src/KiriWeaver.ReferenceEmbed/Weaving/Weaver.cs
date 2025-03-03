using System.Reflection;
using System.IO.Compression;
using Microsoft.Build.Framework;
using Mono.Cecil;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace KiriWeaver.ReferenceEmbed.Weaving;

public class Weaver : Microsoft.Build.Utilities.Task
{
	[Required]
	public required string InputAssembly { get; set; }

	[Required]
	public required string IntermediateAssembly { get; set; }

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
		using (var output = Weave(File.Exists(IntermediateAssembly)
			? IntermediateAssembly
			: InputAssembly)) 
		{
			if (output is null) return false;
			output.Write(OutputAssembly);
		}

		File.Copy(OutputAssembly, IntermediateAssembly, overwrite: true);
		return true;
	}

	private AssemblyDefinition? Weave(string inputPath) {
		try {
			var assembly = AssemblyDefinition.ReadAssembly(inputPath);

			var info = GetEmbedInfo(assembly);
			
			foreach (var taskItem in ReferencePaths) {
				var name = new AssemblyName(taskItem.GetMetadata("FusionName") ?? "!").Name;
				if (!(info.ExcludeMode ^ info.Filter.Remove(name))) continue;

				var embedName = info.Prefix + name;
				bool compress = info.CompressionMap.TryGetValue(name, out var value)
					? value
					: info.DefaultCompression;

				byte[] fileBytes = File.ReadAllBytes(taskItem.ItemSpec);

				EmbeddedResource resource;

				if (!compress) {
					resource = new EmbeddedResource(
						embedName,
						ManifestResourceAttributes.Public, 
						fileBytes);
				} else {
					var stream = new MemoryStream();
					using (var compressStream = new DeflateStream(stream, CompressionLevel.Optimal, true)) {
						compressStream.Write(fileBytes, 0, fileBytes.Length);
					}
					stream.Position = 0;
					resource = new(
						embedName + ".compressed",
						ManifestResourceAttributes.Public,
						stream);
				}
				assembly.MainModule.Resources.Add(resource);
			}
			return assembly;
		}
		catch (Exception ex) {
			Log.LogError($"""
				{nameof(ReferenceEmbed)} weaving failed because {ex.Message}
				StackTrace: 
				{ex.StackTrace}
				""");
			return null;
		}
	}

	private static EmbedInfo GetEmbedInfo(AssemblyDefinition assembly) 
	{
		var Prefix = nameof(ReferenceEmbed);
		HashSet<string> Filter = [];
		bool? ExcludeMode = null;
		var DefaultCompression = false;
		Dictionary<string, bool> CompressionMap = [];
		List<CustomAttribute> removeAttrs = [];

		for (int i = 0; i < assembly.CustomAttributes.Count; i++) {
			var attr = assembly.CustomAttributes[i];
			const string @namespace = $"{nameof(KiriWeaver)}.{nameof(ReferenceEmbed)}";

			if (!attr.AttributeType.FullName.StartsWith(@namespace)) continue;
			switch (attr.AttributeType.FullName[(@namespace.Length + 1)..]) {
				case nameof(EmbedConfigAttribute): {
					var args = attr.ConstructorArguments.Select(arg => arg.Value);
					if (args.FirstOrDefault(arg => arg is bool) is true)
						DefaultCompression = true;
					if (args.FirstOrDefault(arg => arg is string) is string prefix)
						Prefix = prefix;
					break;
				}
				case nameof(EmbedIncludeAllAttribute): 
					ExcludeMode ??= true;
					break;
				case nameof(EmbedIncludeAttribute): {
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
				case nameof(EmbedExcludeAttribute): {
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
				default: continue;
			}
			
			removeAttrs.Add(attr);
		}

		removeAttrs.ForEach(attr => assembly.CustomAttributes.Remove(attr));

		var reference = assembly.MainModule.AssemblyReferences
			.FirstOrDefault(r => r.Name == Assembly.GetExecutingAssembly().GetName().Name);
		if (reference is not null) 
			assembly.MainModule.AssemblyReferences.Remove(reference);

		return new(Prefix + '.', Filter, ExcludeMode ?? false, DefaultCompression, CompressionMap);
	}
}