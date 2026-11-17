// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The tasks of a run that the control plane cancelled while an agent was running them, and whose agent has
/// not yet reported that the process tree stopped (R-12).
/// </summary>
/// <param name="Awaiting">Still inside the maximum wait: the scheduler holds the run's next dispatch back.</param>
/// <param name="TimedOut">Past the maximum wait without an acknowledgement: the scheduler proceeds and logs it.</param>
public readonly record struct CancelledTaskStopWait(int Awaiting, int TimedOut);
