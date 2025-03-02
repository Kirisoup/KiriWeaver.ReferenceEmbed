using System.IO.Compression;
using System.Reflection;
using Microsoft.Build.Framework;
using Mono.Cecil;

namespace KiriWeaver.ReferenceEmbed.Weaving;

public class Weaver : Microsoft.Build.Utilities.Task
{
	[Required]
	public required string InputAssembly { get; set; }

	[Required]
	public required string OutputAssembly { get; set; }

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

	public override bool Execute()
	{
		try {
			// throw new Exception("hi");
            using var assembly = AssemblyDefinition.ReadAssembly(InputAssembly);

			const string @namespace = $"{nameof(KiriWeaver)}.{nameof(ReferenceEmbed)}";

			var info = GetEmbedInfo(TakeEmbedAttrs(assembly));

			GetIncludedFiles(info)
				.Select(file => GetResource(assembly, file, info))
				.Where(resource => resource is not null)
				.ToList()
				.ForEach(assembly.MainModule.Resources.Add);

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

	private static List<AttrInfo> TakeEmbedAttrs(
		AssemblyDefinition assembly)
	{
		List<AttrInfo> embedAttrs = [];
		List<int> removeIdx = [];
		for (int i = 0; i < assembly.CustomAttributes.Count; i++) {
			var attr = assembly.CustomAttributes[i];
			bool remove = true;

			const string @namespace = $"{nameof(KiriWeaver)}.{nameof(ReferenceEmbed)}";
			switch (attr.AttributeType.FullName) {
			case $"{@namespace}.{nameof(EmbedConfigAttribute)}":
				embedAttrs.Add(new(
					AttrType.Config, 
					attr.ConstructorArguments.Select(arg => arg.Value)));
				break;
			case $"{@namespace}.{nameof(EmbedIncludeAllAttribute)}": 
				embedAttrs.Add(new(
					AttrType.IncludeAll,
					attr.ConstructorArguments.Select(arg => arg.Value)));
				break;
			case $"{@namespace}.{nameof(EmbedIncludeAttribute)}":
				embedAttrs.Add(new(
					AttrType.Include, 
					attr.ConstructorArguments.Select(arg => arg.Value)));
				break;
			case $"{@namespace}.{nameof(EmbedExcludeAttribute)}":
				embedAttrs.Add(new(
					AttrType.Exclude, 
					attr.ConstructorArguments.Select(arg => arg.Value)));
				break;
			default: 
				remove = false;
				break;
			}
			if (remove) removeIdx.Add(i);
		}

		for (int i = removeIdx.Count - 1; i >= 0; i--)
			assembly.CustomAttributes.RemoveAt(removeIdx[i]);

		var reference = assembly.MainModule.AssemblyReferences
			.FirstOrDefault(r => r.Name == Assembly.GetExecutingAssembly().GetName().Name);
		if (reference is not null) 
			assembly.MainModule.AssemblyReferences.Remove(reference);

		return embedAttrs;
	}

	private static EmbedInfo GetEmbedInfo(List<AttrInfo> attrs) {
		var Prefix = nameof(ReferenceEmbed);
		HashSet<string> Filter = [];
		bool? ExcludeMode = false;
		var DefaultCompression = false;
		Dictionary<string, bool> CompressionMap = [];

		foreach (var attr in attrs) switch (attr.Type) {
			case AttrType.Config:
				if (attr.Args.FirstOrDefault(arg => arg is bool) is true)
					DefaultCompression = true;
				if (attr.Args.FirstOrDefault(arg => arg is string) is string prefix)
					Prefix = prefix;
				break;
			case AttrType.IncludeAll: 
				ExcludeMode ??= true;
				break;
			case AttrType.Include: {
				if (attr.Args.FirstOrDefault(arg => arg is string) is not string file) break;
				ExcludeMode ??= false;
				var name = Path.GetFileNameWithoutExtension(file);
				if (!ExcludeMode.Value) {
					Filter.Add(name);
				} else {
					Filter.Remove(name);
				}
				if (attr.Args.FirstOrDefault(arg => arg is bool) is not bool compress) break;
				CompressionMap.Add(file, compress);
				break;
			}
			case AttrType.Exclude: {
				if (attr.Args.FirstOrDefault() is not string file) break;
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

		return new(Prefix + '.', Filter, ExcludeMode ?? false, DefaultCompression, CompressionMap);
	} 

	private IEnumerable<string> GetIncludedFiles(EmbedInfo info) {
		var dir = Path.GetDirectoryName(InputAssembly);
		if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(
			$"directory {dir} is not found");
		return Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
			.Concat(Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
			.Where(file => info.ExcludeMode ^ info.Filter.Remove(Path.GetFileNameWithoutExtension(file)));
	}

	private static EmbeddedResource? GetResource(AssemblyDefinition assembly, string path, 
		EmbedInfo info)
	{
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
		return assembly.MainModule.Resources.Any(res => res.Name == embedName)
			? null
			: new(
				embedName,
				ManifestResourceAttributes.Public, 
				fileBytes);
	}
}