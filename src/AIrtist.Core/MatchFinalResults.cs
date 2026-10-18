namespace AIrtist.Core;

public sealed record MatchPlayerTotal(string PlayerId, string Name, int Votes);
public sealed record MatchFinalResults(int RoundCount, int TotalVotes,
    IReadOnlyList<MatchPlayerTotal> Players, IReadOnlyList<MatchPlayerTotal> Winners)
{
    // Use stable player IDs: two people may choose the same display name.
    public static MatchFinalResults Create(IReadOnlyList<VotingPlayer> players, IReadOnlyList<MatchRoundRecord> rounds)
    {
        if (players.Count is < 2 or > 16 || rounds.Count is < 1 or > 10 ||
            players.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != players.Count)
            throw new ArgumentException("경기 결과의 참가자·라운드 정보를 확인해 주세요.");
        var totals = players.ToDictionary(p => p.Id, _ => 0, StringComparer.Ordinal);
        for (int i = 0; i < rounds.Count; i++)
        {
            var record = rounds[i]; var result = record.Result;
            if (record.RoundNumber != i + 1 || result.MatchId != rounds[0].Result.MatchId ||
                result.Tallies.Select(t => t.OwnerId).Distinct(StringComparer.Ordinal).Count() != result.Tallies.Count ||
                result.Tallies.Any(t => !totals.ContainsKey(t.OwnerId) || t.Votes < 0) ||
                result.Tallies.Sum(t => (long)t.Votes) != result.AcceptedVotes)
                throw new ArgumentException("확정된 라운드 결과가 올바르지 않습니다.");
            foreach (var tally in result.Tallies) totals[tally.OwnerId] = checked(totals[tally.OwnerId] + tally.Votes);
        }
        var scores = players.Select(p => new MatchPlayerTotal(p.Id, p.Name, totals[p.Id])).OrderByDescending(p => p.Votes).ToArray();
        int maximum = scores[0].Votes;
        return new(rounds.Count, scores.Sum(p => p.Votes), Array.AsReadOnly(scores),
            Array.AsReadOnly(maximum == 0 ? [] : scores.Where(p => p.Votes == maximum).ToArray()));
    }
}
