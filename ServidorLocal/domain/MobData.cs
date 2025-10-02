namespace ServidorLocal.Domain;

public readonly record struct MobData(
    string idmob,
    float posx,
    float posy,
    float life,
    int tipo,
    int area
);
