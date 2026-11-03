using System;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace TemporalCommunity.Extensions.AI.Tests;

internal sealed class RejectingPayloadConverter : IPayloadConverter
{
    private readonly IPayloadConverter inner = DataConverter.Default.PayloadConverter;

    public Payload ToPayload(object? value) => inner.ToPayload(value);

    public object? ToValue(Payload payload, Type type) => null;
}
