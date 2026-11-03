using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using static System.String;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace ML_OsuDataFormatter;

class Program
{
    private static RealmAccess realm = null!;

    static void Main(string[] args)
    {
        Storage storage = new NativeStorage("./");
        realm = new RealmAccess(storage, "client.realm");
        var realmScores = realm.Run(r =>
            r.All<ScoreInfo>()
                .Detach()
                .Where(s => s.RealmUser.Username is "-Dani" or "Dani211e")
                .Where(s => s.RulesetID == 0)
                .Where(s => s.Passed)
                .Where(s => s.Rank > ScoreRank.F)
                .Where(s => s.Accuracy > 0)
                .Where(s => s.BeatmapInfo != null)
                .Where(s => s.BeatmapInfo!.StarRating <= 10) // Aspire maps kinda break stuff, so excluded
                .Where(s => s.BeatmapInfo!.StarRating > 1)
                .OrderBy(s => s.Date)
        ).ToList();

        List<Score> scores = [];
        var prevScore = realmScores[0];
        // Since we want to calculate the time difference between scores we just skip the first score
        int count = 0;
        foreach (var realmScore in realmScores.Skip(1))
        {
            if (count % 250 == 0)
                Console.WriteLine($"Progress: {count}/{realmScores.Count}");
            scores.Add(new Score(realmScore, prevScore));
            prevScore = realmScore;
            count++;
        }

        Console.WriteLine($"Found {scores.Count} valid scores");
        WriteToCsv(scores);
        Console.WriteLine("Finished writing to scores.csv");
    }

    private static void WriteToCsv(List<Score> scores)
    {
        var fields = typeof(Score).GetFields(BindingFlags.Public | BindingFlags.Instance);
        var columns = Join(",", fields.Select(f => f.Name));
        using var file = new StreamWriter("scores.csv");

        file.WriteLine(columns);
        foreach (var score in scores)
        {
            var stringBuilder = new StringBuilder();
            for (var i = 0; i < fields.Length - 1; ++i)
                stringBuilder.Append(fields[i].GetValue(score) + ",");

            stringBuilder.Append(fields[^1].GetValue(score));
            file.WriteLine(stringBuilder.ToString());
        }
    }

    private static List<Score> FetchSqlScores()
    {
        var sqlStream = new FileStream("./formatted.txt", FileMode.Open, FileAccess.Read, FileShare.Read);

        using var reader = new StreamReader(sqlStream);
        List<Score> sqlScores = [];
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line == null)
                break;

            //Trim first and last char for ( )
            sqlScores.Add(new Score(line.Substring(1, line.Length - 2)));
        }

        return sqlScores;
    }


    /*
CREATE TABLE `scores` (
    `id` bigint unsigned NOT NULL AUTO_INCREMENT,
    `user_id` int unsigned NOT NULL,
    `ruleset_id` smallint unsigned NOT NULL,
    `beatmap_id` mediumint unsigned NOT NULL,
    `has_replay` tinyint(1) NOT NULL DEFAULT '0',
    `preserve` tinyint(1) NOT NULL DEFAULT '0',
    `ranked` tinyint(1) NOT NULL DEFAULT '1',
    `rank` char(2) NOT NULL DEFAULT '',
    `passed` tinyint NOT NULL DEFAULT '0',
    `accuracy` float NOT NULL DEFAULT '0',
    `max_combo` int unsigned DEFAULT '0',
    `total_score` int unsigned NOT NULL DEFAULT '0',
    `data` json NOT NULL,
    `pp` float unsigned DEFAULT NULL,
    `legacy_score_id` bigint unsigned DEFAULT NULL,
    `legacy_total_score` int unsigned NOT NULL DEFAULT '0',
    `started_at` timestamp NULL DEFAULT NULL,
    `ended_at` timestamp NOT NULL,
    `unix_updated_at` int unsigned NOT NULL DEFAULT (unix_timestamp()),
    `build_id` smallint unsigned DEFAULT NULL,
    )
     */
    public class Score
    {
        public long ScoreID;
        public long BeatmapID;

        public float Accuracy;
        public int Combo;
        public int TotalScore;

        public int Count300;
        public int Count100;
        public int Count50;
        public int CountMiss;
        public int CountSliderTick;
        public int CountSliderEnd;

        public int MaxGreat;
        public int MaxSliderTick;
        public int MaxSliderEnd;
        public int MaxCombo;

        // public long TotalScoreWithoutMods;

        public float OverallDifficulty;
        public float ApproachRate;
        public float CircleSize;
        public double StarRating;
        public double AimDifficulty;
        public double SpeedDifficulty;
        public double ReadingDifficulty;

        public double Length;
        public double Bpm;

        // in minutes
        public double TimeDifference;
        public DateTimeOffset Date;

        public double Pp;

        public Score(string line)
        {
            var split = SplitLine(line);

            // Id = long.Parse(split[0]);
            // UserId = int.Parse(split[1]);
            // RulesetId = int.Parse(split[2]);
            // BeatmapId = int.Parse(split[3]);
            // HasReplay = int.Parse(split[4]) == 1;
            // Preserve = int.Parse(split[5]) == 1;
            // Ranked = int.Parse(split[6]) == 1;
            // Rank = split[7];
            // Passed = int.Parse(split[8]) == 1;
            Accuracy = float.Parse(split[9]);
            MaxCombo = int.Parse(split[10]);
            TotalScore = int.Parse(split[11]);

            var temp = split[12].Replace("\\\"", "\"");
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
            };
            var json = JsonSerializer.Deserialize<JsonBlob>(temp, options) ??
                       throw new ArgumentException("Invalid json blob");
            // Mods = json.Mods;
            // Statistics = json.Statistics;
            // MaximumStatistics = json.MaximumStatistics;
            // TotalScoreWithoutMods = json.TotalScoreWithoutMods;

            // LegacyScoreId = split[14] == "NULL" ? null : long.Parse(split[14]);
            // LegacyTotalScore = int.Parse(split[15]);
            // EndedAt = DateTime.ParseExact(split[17], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string[] SplitLine(ReadOnlySpan<char> line)
        {
            List<string> splits = [];
            // '{\"mods\": [{\"acronym\": \"DA\", \"settings\": {\"approach_rate\": 9.7}}], \"statistics\": {\"ok\": 33, \"meh\": 12, \"miss\": 15, \"great\": 302, \"ignore_hit\": 158, \"large_tick_hit\": 325, \"slider_tail_hit\": 158}, \"maximum_statistics\": {\"great\": 362, \"ignore_hit\": 158, \"large_tick_hit\": 325, \"slider_tail_hit\": 158}, \"total_score_without_mods\": 477094}'
            // '2024-07-29 08:25:22'
            var start = 0;
            var end = 0;

            var depth = 0;
            while (start != line.Length)
            {
                if (end == line.Length)
                {
                    splits.Add(line.Slice(start, end - start).ToString().Replace("\'", ""));
                    break;
                }

                if (depth == 0 && line[end] == ',')
                {
                    splits.Add(line.Slice(start, end - start).ToString().Replace("\'", ""));
                    ++end;
                    start = end;
                    continue;
                }

                if (line[end] == '{')
                    ++depth;
                if (line[end] == '}')
                    --depth;

                ++end;

                if (end > 100000)
                    throw new UnreachableException("Something really bad happened");
            }

            if (depth != 0)
                throw new InvalidOperationException("Depth finished != 0");

            return splits.ToArray();
        }

        public Score(ScoreInfo scoreInfo, ScoreInfo prevScore)
        {
            ScoreID = scoreInfo.IsLegacyScore ? scoreInfo.LegacyOnlineID : scoreInfo.OnlineID;
            BeatmapID = scoreInfo.BeatmapInfo!.OnlineID;

            Accuracy = (float)scoreInfo.Accuracy;
            Combo = scoreInfo.MaxCombo;
            TotalScore = (int)scoreInfo.TotalScore;

            // Mods = scoreInfo.APIMods;
            // TotalScoreWithoutMods = scoreInfo.TotalScoreWithoutMods;

            // LegacyScoreId = scoreInfo.LegacyOnlineID;
            // LegacyTotalScore = scoreInfo.LegacyTotalScore;

            var stats = scoreInfo.Statistics;
            var maxStats = scoreInfo.MaximumStatistics;
            // This doesnt handle CL very well, womp womp
            stats.TryGetValue(HitResult.Great, out Count300);
            stats.TryGetValue(HitResult.Ok, out Count100);
            stats.TryGetValue(HitResult.Meh, out Count50);
            stats.TryGetValue(HitResult.Miss, out CountMiss);
            stats.TryGetValue(HitResult.LargeTickHit, out CountSliderTick);
            stats.TryGetValue(HitResult.SliderTailHit, out CountSliderEnd);
            maxStats.TryGetValue(HitResult.Great, out MaxGreat);
            maxStats.TryGetValue(HitResult.LargeTickHit, out MaxSliderTick);
            maxStats.TryGetValue(HitResult.SliderTailHit, out MaxSliderEnd);

            //TODO: these need to be scaled by mods
            var info = scoreInfo.BeatmapInfo!;
            MaxCombo = scoreInfo.GetMaximumAchievableCombo();
            OverallDifficulty = info.Difficulty.OverallDifficulty;
            ApproachRate = info.Difficulty.ApproachRate;
            CircleSize = info.Difficulty.CircleSize;
            StarRating = info.StarRating;
            Length = info.Length;
            Bpm = info.BPM;

            TimeDifference = (scoreInfo.Date - prevScore.Date).TotalMinutes;
            Date = scoreInfo.Date;

            var rulesetInstance = new OsuRuleset();
            const string osuBasePath = "C:/Users/danie/AppData/Roaming/osu/files";
            var filename = scoreInfo.BeatmapHash;
            var path = Path.Combine(osuBasePath, filename[0].ToString(),
                filename[0].ToString() + filename[1].ToString(),
                filename);

            AimDifficulty = -1;
            SpeedDifficulty = -1;
            ReadingDifficulty = -1;

            if (!Path.Exists(path))
            {
                Console.WriteLine($"Beatmap {scoreInfo.BeatmapInfo!.Path} not found");
                return;
            }

            var working = new FlatWorkingBeatmap(path);
            var diffCalc = rulesetInstance.CreateDifficultyCalculator(working);
            var diffAttr = (OsuDifficultyAttributes)diffCalc.Calculate(scoreInfo.Mods);
            AimDifficulty = diffAttr.AimDifficulty;
            SpeedDifficulty = diffAttr.SpeedDifficulty;
            ReadingDifficulty = diffAttr.ReadingDifficulty;

            var perfCalc = rulesetInstance.CreatePerformanceCalculator();
            var perfAttr = perfCalc.Calculate(scoreInfo, diffAttr);
            Pp = perfAttr.Total;
        }
    }

    public class JsonBlob
    {
        [JsonPropertyName("mods")]
        public APIMod[] Mods { get; set; } = null!;

        [JsonPropertyName("statistics")]
        public Dictionary<HitResult, int> Statistics { get; set; } = null!;

        [JsonPropertyName("maximum_statistics")]
        public Dictionary<HitResult, int> MaximumStatistics { get; set; } = null!;

        [JsonPropertyName("total_score_without_mods")]
        public int TotalScoreWithoutMods { get; set; }
    }
}