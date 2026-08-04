using ShardingLab.Phases;

while (true)
{
    Console.WriteLine("\n=== Sharding Laboratory ===");
    Console.WriteLine(" 1) Phase  1 - Single Database");
    Console.WriteLine(" 2) Phase  2 - The Database Gets Huge");
    Console.WriteLine(" 3) Phase  3 - Introduce Sharding");
    Console.WriteLine(" 4) Phase  4 - Shard Key Strategies");
    Console.WriteLine(" 5) Phase  5 - Shard Router as an Abstraction");
    Console.WriteLine(" 6) Phase  6 - Cross-Shard Queries");
    Console.WriteLine(" 7) Phase  7 - Hot Shards");
    Console.WriteLine(" 8) Phase  8 - Resharding");
    Console.WriteLine(" 9) Phase  9 - Consistent Hashing");
    Console.WriteLine("10) Phase 10 - Replication");
    Console.WriteLine("11) Phase 11 - Replication Lag");
    Console.WriteLine("12) Phase 12 - Shard Failure");
    Console.WriteLine("13) Phase 13 - Rebalancing");
    Console.WriteLine("14) Phase 14 - Monitoring");
    Console.WriteLine("15) Phase 15 - Benchmark");
    Console.WriteLine(" q) Quit");
    Console.Write("> ");

    var choice = Console.ReadLine();
    Console.WriteLine();

    switch (choice)
    {
        case "1": new Phase01_SingleDb().Run(); break;
        case "2": new Phase02_ScaleLimits().Run(); break;
        case "3": new Phase03_Sharding().Run(); break;
        case "4": new Phase04_ShardKeyStrategies().Run(); break;
        case "5": new Phase05_RouterAbstraction().Run(); break;
        case "6": new Phase06_CrossShardQueries().Run(); break;
        case "7": new Phase07_HotShards().Run(); break;
        case "8": new Phase08_Resharding().Run(); break;
        case "9": new Phase09_ConsistentHashing().Run(); break;
        case "10": new Phase10_Replication().Run(); break;
        case "11": new Phase11_ReplicationLag().Run(); break;
        case "12": new Phase12_ShardFailure().Run(); break;
        case "13": new Phase13_Rebalancing().Run(); break;
        case "14": new Phase14_Monitoring().Run(); break;
        case "15": new Phase15_Benchmark().Run(); break;
        case "q":
        case "Q":
            return;
        default:
            Console.WriteLine("Unknown choice.");
            break;
    }
}
