namespace ServidorLocal.Domain;

public readonly record struct PlayerData(
    string idplayer,
    float posx,
    float posy,
    string mapa,
    PlayerStatus status
);



public readonly record struct PlayerStatus(
    int vida,        // Vida atual do jogador
    int vidamax   // Vida máxima
);
