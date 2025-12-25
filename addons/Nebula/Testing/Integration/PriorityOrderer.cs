namespace Nebula.Testing.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

[AttributeUsage(AttributeTargets.Method)]
public class OrderAttribute : Attribute
{
    public int Value { get; }
    public OrderAttribute(int value) => Value = value;
}

public class PriorityOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        return testCases.OrderBy(tc =>
            tc.TestMethod.Method
                .GetCustomAttributes(typeof(OrderAttribute))
                .FirstOrDefault()
                ?.GetNamedArgument<int>(nameof(OrderAttribute.Value)) ?? int.MaxValue);
    }
}