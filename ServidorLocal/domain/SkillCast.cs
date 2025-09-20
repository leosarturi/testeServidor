namespace ServidorLocal.Domain;

// Mensagem final (normalizada) que o servidor usa para broadcast
public readonly record struct SkillCast(
    string idplayer,
    string action,   // "aa" | "s1" | "s2"
    float dx,        // direção normalizada ou enviada pelo cliente
    float dy,
    long tsUtcMs     // timestamp gerado no servidor
);

// Payload de entrada enviado pelo cliente (idplayer será ignorado/forçado)
public readonly record struct SkillCastInput(
    string? action,
    float dx,
    float dy
);
