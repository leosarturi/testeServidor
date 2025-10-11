namespace ServidorLocal.Domain;

public readonly record struct MobData(
    string idmob,
    float posx,
    float posy,
    float life,
    float maxlife,
    int tipo,
    int area
);

public readonly record struct MobLoot(
    int currency,
    int item
);