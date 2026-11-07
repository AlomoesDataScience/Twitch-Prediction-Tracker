namespace PredictionBot;

/// One choice in one resolved prediction, i.e. one bet you could have placed.
/// Share is that choice's fraction of the pool at close (the crowd's implied win probability);
/// Mult is what a winning bet on it returned per point staked (pool / points on that choice).
record Sample(int Position, double Share, double Mult, bool Won, bool Favourite, bool Underdog);

/// Mean/Lo/Hi are the Bayesian posterior win rate and its 95% credible interval.
/// Roi is the observed return of staking 1 point on every bet in the group.
/// BayesRoi is the posterior-expected return per point; PEdge is the posterior probability
/// that the return is positive (see PosteriorReturn).
record GroupStat(string Name, int N, int Wins, double Mean, double Lo, double Hi,
                 double Crowd, double Roi, double BayesRoi, double PEdge);

static class Stats
{
    public const double LowBelow = 0.30, HighAbove = 0.70;

    // Weight of the prior, in pseudo-bets. The prior is centred on the crowd's implied
    // probability, so with little data the answer is "the pool is fairly priced, no edge".
    const double PriorStrength = 2;

    public static List<Sample> Samples(IEnumerable<PredictionRow> predictions)
    {
        var samples = new List<Sample>();
        foreach (var p in predictions)
        {
            if (p.Status != "RESOLVED" || p.WinningOutcomeId is null || p.Outcomes.Count < 2) continue;
            double pool = p.Outcomes.Sum(o => o.Points);
            if (pool <= 0) continue;

            var favourite = p.Outcomes.MaxBy(o => o.Points)!;
            var underdog = p.Outcomes.MinBy(o => o.Points)!;
            foreach (var o in p.Outcomes)
            {
                if (o.Points <= 0) continue; // nobody bet on it, so it has no payout ratio
                samples.Add(new Sample(o.Position, o.Points / pool, pool / o.Points,
                    o.Id == p.WinningOutcomeId, o == favourite, o == underdog && o != favourite));
            }
        }
        return samples;
    }

    public static (List<GroupStat> ByOdds, List<GroupStat> ByChoice) Compute(IEnumerable<PredictionRow> predictions)
    {
        var s = Samples(predictions);

        var byOdds = new List<GroupStat>
        {
            Group($"Low odds (<{LowBelow * 100:0}% of pool)", s.Where(x => x.Share < LowBelow)),
            Group("Medium odds", s.Where(x => x.Share >= LowBelow && x.Share <= HighAbove)),
            Group($"High odds (>{HighAbove * 100:0}% of pool)", s.Where(x => x.Share > HighAbove)),
        };

        var byChoice = new List<GroupStat>
        {
            Group("Crowd favourite", s.Where(x => x.Favourite)),
            Group("Underdog", s.Where(x => x.Underdog)),
        };
        foreach (var position in s.Select(x => x.Position).Distinct().Order())
            byChoice.Add(Group($"Option {position + 1}", s.Where(x => x.Position == position)));

        return (byOdds, byChoice);
    }

    public static GroupStat Group(string name, IEnumerable<Sample> source)
    {
        var s = source.ToList();
        if (s.Count == 0) return new GroupStat(name, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        int n = s.Count, wins = s.Count(x => x.Won);
        var crowd = Math.Clamp(s.Average(x => x.Share), 0.01, 0.99);
        double a = PriorStrength * crowd + wins, b = PriorStrength * (1 - crowd) + (n - wins);
        var (bayesRoi, pEdge) = PosteriorReturn(s);

        return new GroupStat(name, n, wins, a / (a + b),
            BetaQuantile(0.025, a, b), BetaQuantile(0.975, a, b), crowd,
            Roi: s.Sum(x => x.Won ? x.Mult : 0) / n - 1,
            BayesRoi: bayesRoi,
            PEdge: pEdge);
    }

    /// Posterior of the group's return per point staked, by simulation.
    /// A group can mix 20x long shots with 1.1x favourites, and those do not share a win rate, so
    /// bets are first split into bands of similar payout (within ~1.4x of each other). Each band
    /// gets its own crowd-centred Beta posterior; a draw from every band gives one plausible
    /// overall return. Returns the average draw and the fraction of draws that made money.
    static (double Roi, double PProfit) PosteriorReturn(List<Sample> s)
    {
        var bands = s.GroupBy(x => (int)Math.Floor(Math.Log2(x.Mult) * 2)).Select(g =>
        {
            // Centre the prior on the band's break-even win rate, so that with no evidence the
            // expected return is exactly zero rather than quietly favouring long shots.
            var mult = g.Average(x => x.Mult);
            var crowd = Math.Clamp(1 / mult, 0.001, 0.999);
            int n = g.Count(), wins = g.Count(x => x.Won);
            return (N: n, Mult: mult,
                    A: PriorStrength * crowd + wins, B: PriorStrength * (1 - crowd) + (n - wins));
        }).ToList();

        const int draws = 4000;
        var rng = new Random(12345); // fixed seed so the numbers don't flicker between refreshes
        double total = 0;
        var profitable = 0;
        for (var i = 0; i < draws; i++)
        {
            double returned = 0;
            foreach (var band in bands) returned += band.N * SampleBeta(rng, band.A, band.B) * band.Mult;
            var roi = returned / s.Count - 1;
            total += roi;
            if (roi > 0) profitable++;
        }
        return (total / draws, (double)profitable / draws);
    }

    static double SampleBeta(Random rng, double a, double b)
    {
        double x = SampleGamma(rng, a), y = SampleGamma(rng, b);
        return x + y > 0 ? x / (x + y) : 0.5;
    }

    // Marsaglia-Tsang.
    static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1) return SampleGamma(rng, shape + 1) * Math.Pow(1 - rng.NextDouble(), 1 / shape);
        double d = shape - 1.0 / 3, c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double z = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            var v = 1 + c * z;
            if (v <= 0) continue;
            v = v * v * v;
            var u = 1 - rng.NextDouble();
            if (Math.Log(u) < 0.5 * z * z + d - d * v + d * Math.Log(v)) return d * v;
        }
    }

    /// Highest posterior-expected return among groups with enough history to mean anything.
    public static GroupStat? Best(IEnumerable<GroupStat> groups, int minSamples = 5) =>
        groups.Where(g => g.N >= minSamples).MaxBy(g => g.BayesRoi);

    // ---- Beta distribution ------------------------------------------------------------------

    static double BetaQuantile(double p, double a, double b)
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (BetaCdf(mid, a, b) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// Regularized incomplete beta function I_x(a, b).
    static double BetaCdf(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2)
            ? front * ContinuedFraction(x, a, b) / a
            : 1 - front * ContinuedFraction(1 - x, b, a) / b;
    }

    static double ContinuedFraction(double x, double a, double b)
    {
        const double eps = 3e-14, tiny = 1e-300;
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1 / d;
        var h = d;
        for (var m = 1; m <= 500; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < eps) break;
        }
        return h;
    }

    static double LogGamma(double x)
    {
        double[] coefficients =
        [
            76.18009172947146, -86.50532032941677, 24.01409824083091,
            -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
        ];
        var y = x;
        var tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        var series = 1.000000000190015;
        foreach (var coefficient in coefficients) series += coefficient / ++y;
        return -tmp + Math.Log(2.5066282746310005 * series / x);
    }
}
