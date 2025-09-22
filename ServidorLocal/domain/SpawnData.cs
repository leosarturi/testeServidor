namespace ServidorLocal.Domain;

public readonly record struct SpawnData(
    float posx,
    float posy,
    int spawnedMob,
    int lastSpawnedTime,
    MobData[] mobs
);
