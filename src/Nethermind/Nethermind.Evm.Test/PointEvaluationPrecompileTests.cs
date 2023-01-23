// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class PointEvaluationPrecompileTests
{
    [TestCaseSource(nameof(TestCases))]
    public void Test_PointEvaluationPrecompile_Produces_Correct_Outputs(string testName, byte[] input, bool expectedResult, byte[] expectedOutput)
    {
        IPrecompile precompile = PointEvaluationPrecompile.Instance;

        (ReadOnlyMemory<byte> output, bool success) = precompile.Run(input, Cancun.Instance);

        output.ToArray().Should().BeEquivalentTo(expectedOutput, testName);
        success.Should().Be(expectedResult, testName);
    }


    [TestCaseSource(nameof(TestCases))]
    public void Test_PointEvaluationPrecompile_Has_Specific_Constant_Gas_Cost(string testName, byte[] input, bool expectedResult, byte[] expectedOutput)
    {
        const long fixedGasCost = 50000;
        IPrecompile precompile = PointEvaluationPrecompile.Instance;

        (ReadOnlyMemory<byte> output, bool success) = precompile.Run(input, Cancun.Instance);
        long gasSpent = precompile.DataGasCost(input, Cancun.Instance) + precompile.BaseGasCost(Cancun.Instance);

        gasSpent.Should().Be(fixedGasCost, testName);
    }

    private static readonly byte[] _predefinedSuccessAnswer = Bytes.FromHexString("001000000000000001000000fffffffffe5bfeff02a4bd5305d8a10908d83933487d9d2953a7ed73");
    private static readonly byte[] _predefinedFailureAnswer = Array.Empty<byte>();

    public static IEnumerable TestCases
    {
        get
        {
            yield return new object[] {
                "Incorrect data size",
                Bytes.FromHexString("00"),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Incorrect data size",
                Array.Empty<byte>(),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Hash is not correctly versioned",
                Bytes.FromHexString("ff0657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Z is out of range",
                Bytes.FromHexString("010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000ff0000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Y is out of range",
                Bytes.FromHexString("010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000ffc00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Commitment does not much hash",
                Bytes.FromHexString("010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c10000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Proof does not match",
                Bytes.FromHexString("010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c20000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                false,
                _predefinedFailureAnswer
            };
            yield return new object[] {
                "Verification passes",
                Bytes.FromHexString("010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"),
                true,
                _predefinedSuccessAnswer
            };
        }
    }
}
