using System.Text.Json.Serialization;

namespace DiscordVoiceWidget.Rpc;

/// <summary>
/// Serializacao gerada em tempo de compilacao para os poucos tipos que vao ao disco.
///
/// O serializador por reflexao precisa inspecionar os tipos em tempo de execucao,
/// o que custa JIT e metadados residentes na memoria de um processo que fica aberto
/// o dia todo. O app desliga a reflexao de vez
/// (JsonSerializerIsReflectionEnabledByDefault=false), entao um tipo novo que for
/// serializado precisa ser registrado aqui.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OAuthToken))]
[JsonSerializable(typeof(DiscordCredentials))]
internal sealed partial class RpcJsonContext : JsonSerializerContext;
