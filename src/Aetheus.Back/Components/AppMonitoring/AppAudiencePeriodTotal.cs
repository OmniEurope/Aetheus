// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary> The audience aggregates of one period (the current day, week or month) added up over
/// several applications. </summary>
public sealed record AppAudiencePeriodTotal(
    AnalyticsPeriodKind Kind,
    int UniqueVisitors,
    int AuthenticatedUniqueVisitors,
    int Sessions,
    long PageViews);
