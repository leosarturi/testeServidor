using System.Security.Cryptography.X509Certificates;

namespace ServidorLocal.Domain;

// Torna imutável após criado (init-only)
public record struct SpawnData
{
    public float PosX { get; set; }
    public float PosY { get; set; }
    public int SpawnedMob { get; set; }
    public int LastSpawnedTime { get; set; }
    public MobData[] Mobs { get; set; }
    public string Mapa { get; set; }

    public SpawnData(float posX, float posY, int spawnedMob, int lastSpawnedTime, MobData[]? mobs, string mapa)
    {
        PosX = posX;
        PosY = posY;
        SpawnedMob = spawnedMob;
        LastSpawnedTime = lastSpawnedTime;
        Mobs = mobs ?? Array.Empty<MobData>();
        Mapa = mapa;
    }
}

