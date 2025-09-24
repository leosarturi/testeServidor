namespace ServidorLocal.Domain;

public readonly record struct PlayerData(
    string idplayer,
    float posx,
    float posy,
    string mapa
);
