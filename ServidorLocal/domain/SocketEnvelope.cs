namespace ServidorLocal.Domain;

public readonly record struct SocketEnvelope<T>(
    string type,
    T data
);

// Para leitura rápida do tipo sem desserializar tudo
public readonly record struct EnvelopeTypeOnly(string type);
