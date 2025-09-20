namespace ServidorLocal.Domain;

public readonly record struct MobData(
    string idmob,
    float posx,
    float posy,
    int life
);
