// Minimal SDK contracts for this offline, source-linked behavioral regression.
// These do not validate the production SDK package ABI; no platform implementation is mocked inside the module.
using System.Text.Json.Nodes;
namespace MyPowerTools.Protocol { public class Stub {} }
namespace MyPowerTools.Abstractions {
 public interface IMptModule {} public interface IMptModuleLifecycle {}
 public class ModuleContext { public string DataDirectory="",CacheDirectory="",LogDirectory=""; public string ProtocolVersion="1"; public bool TryGetCapability<T>(string s,out T? t){t=default;return false;} }
 public record InitializeResult(bool Success,string ProtocolVersion,string[] Features);
 public record HealthCheckSnapshot(string Id,string Name,bool Ok,string Message);
 public record ModuleStatusSnapshot(string Id,string State,string Summary,DateTimeOffset Time,List<HealthCheckSnapshot> Checks,ulong Seq);
 public record MptModuleEvent(string Id,ulong Seq,string Type,DateTimeOffset Time,JsonObject Values);
 public record EventCursor(ulong LastEventSeq);
 public record SettingsSchemaDocument(string Id,string Schema);
 public record SettingsSnapshotDocument(string Id,int Revision,JsonObject Values,DateTimeOffset Time);
 public record SettingsPatch(JsonObject Patch);
 public record SettingsValidationResult(bool Ok,List<string> Messages,MptRuntimeError? Error);
 public record MptRuntimeError(string Code,string Message);
 public record UiSurfaceDescriptor(string Id,string Kind,string Title,JsonObject Values);
 public record CommandRequest(string InvocationId,string CommandId);
 public record CommandExecutionResult(string InvocationId,string CommandId,string State,bool Success,string Output,MptRuntimeError? Error=null);
 public record MptCommandDescriptor(string Id,string ModuleId,string Title,string Subtitle,string Kind,string Category,int TimeoutMs,string[] Constraints,bool SupportsCancellation);
 public static class MptOperationConstraints { public const string RunsExternalProcesses="runs-process"; }
 public static class MptErrorCodes { public const string NotFound="not-found", RuntimeUnavailable="unavailable", ValidationFailed="invalid"; }
 public static class SettingsJson { public static string? ReadString(JsonObject o,string k)=>o[k]?.GetValue<string>(); public static bool? ReadBool(JsonObject o,string k)=>o[k]?.GetValue<bool>(); public static JsonObject Merge(JsonObject a,JsonObject b){var c=(JsonObject)a.DeepClone();foreach(var p in b)c[p.Key]=p.Value?.DeepClone();return c;} }
}
namespace MyPowerTools.Platform.Abstractions {
 public sealed record KeyboardShortcutResult(bool Success,string Code,string Message);
 public interface IKeyboardShortcutService { Task<KeyboardShortcutResult> SendAsync(string gesture,CancellationToken cancellationToken); }
}
namespace Screenshot.MyPowerTools {
 internal static class ScreenshotWindowsShortcut { public static bool TrySend(string gesture,out string error) { throw new InvalidOperationException("Native input must not run in this synthetic regression."); } }
}
