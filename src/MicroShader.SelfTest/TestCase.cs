namespace MicroShader.SelfTest;

/// <summary>一个自检用例。</summary>
internal sealed class TestCase
{
    public TestCase(string suite, string name, Action body)
    {
        Suite = suite;
        Name = name;
        Body = body;
    }

    public string Suite { get; }

    public string Name { get; }

    public Action Body { get; }

    public string FullName => Suite + "." + Name;
}

/// <summary>用例登记与执行。</summary>
internal static class TestSuite
{
    private static readonly List<TestCase> Cases = new();

    public static void Add(string suite, string name, Action body) => Cases.Add(new TestCase(suite, name, body));

    public static IReadOnlyList<TestCase> All => Cases;

    public static int Run(string? filter, bool verbose)
    {
        var selected = Cases
            .Where(c => filter is null || c.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var pass = 0;
        var fail = 0;
        var skip = 0;
        var currentSuite = string.Empty;

        foreach (var testCase in selected)
        {
            if (!string.Equals(currentSuite, testCase.Suite, StringComparison.Ordinal))
            {
                currentSuite = testCase.Suite;
                Console.WriteLine();
                Console.WriteLine($"── {currentSuite} " + new string('─', Math.Max(0, 56 - currentSuite.Length)));
            }

            try
            {
                testCase.Body();
                pass++;
                if (verbose)
                {
                    Console.WriteLine($"  PASS  {testCase.Name}");
                }
                else
                {
                    Console.Write('.');
                }
            }
            catch (SkipException skipException)
            {
                skip++;
                Console.WriteLine();
                Console.WriteLine($"  SKIP  {testCase.Name}  ({skipException.Message})");
            }
            catch (AssertionException assertion)
            {
                fail++;
                Console.WriteLine();
                Console.WriteLine($"  FAIL  {testCase.Name}");
                Console.WriteLine("        " + assertion.Message.Replace("\n", "\n        ", StringComparison.Ordinal));
            }
            catch (Exception unexpected)
            {
                fail++;
                Console.WriteLine();
                Console.WriteLine($"  FAIL  {testCase.Name}  (意外异常)");
                Console.WriteLine("        " + unexpected);
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"合计: {selected.Length}  通过: {pass}  失败: {fail}  跳过: {skip}");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>跳过用例（环境缺失），不算失败。</summary>
internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message)
    {
    }
}
