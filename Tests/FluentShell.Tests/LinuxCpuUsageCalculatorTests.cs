using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class LinuxCpuUsageCalculatorTests
{
    [TestMethod]
    public void AddSample_returns_null_for_the_baseline()
    {
        var calculator = new LinuxCpuUsageCalculator();

        var usage = calculator.AddSample("cpu  100 20 30 800 50 10 5 15 0 0");

        Assert.IsNull(usage);
    }

    [TestMethod]
    public void AddSample_calculates_usage_from_total_and_idle_deltas()
    {
        var calculator = new LinuxCpuUsageCalculator();
        _ = calculator.AddSample("cpu  100 20 30 800 50 10 5 15 0 0");

        var usage = calculator.AddSample("cpu  130 30 50 840 70 25 15 30 0 0");

        Assert.AreEqual(62.5, usage!.Value, 0.001);
    }

    [TestMethod]
    public void AddSample_includes_nice_irq_softirq_and_steal_without_counting_guest_twice()
    {
        var calculator = new LinuxCpuUsageCalculator();
        _ = calculator.AddSample("cpu  100 20 30 800 50 10 5 15 70 40");

        var usage = calculator.AddSample("cpu  110 25 35 810 55 15 10 20 90 55");

        Assert.AreEqual(70, usage!.Value, 0.001);
    }

    [TestMethod]
    public void AddSample_ignores_invalid_samples_without_replacing_the_baseline()
    {
        var calculator = new LinuxCpuUsageCalculator();
        _ = calculator.AddSample("cpu  100 20 30 800 50 10 5 15 0 0");

        Assert.IsNull(calculator.AddSample("cpu not-a-number"));
        var usage = calculator.AddSample("cpu  110 25 35 810 55 15 10 20 0 0");

        Assert.AreEqual(70, usage!.Value, 0.001);
    }
}