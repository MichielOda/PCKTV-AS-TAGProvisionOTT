/*
****************************************************************************
*  Copyright (c) 2022,  Skyline Communications NV  All Rights Reserved.    *
****************************************************************************

By using this script, you expressly agree with the usage terms and
conditions set out below.
This script and all related materials are protected by copyrights and
other intellectual property rights that exclusively belong
to Skyline Communications.

A user license granted for this script is strictly for personal use only.
This script may not be used in any way by anyone without the prior
written consent of Skyline Communications. Any sublicensing of this
script is forbidden.

Any modifications to this script by the user are only allowed for
personal use and within the intended purpose of the script,
and will remain the sole responsibility of the user.
Skyline Communications will not be responsible for any damages or
malfunctions whatsoever of the script resulting from a modification
or adaptation by the user.

The content of this script is confidential information.
The user hereby agrees to keep this confidential information strictly
secret and confidential and not to disclose or reveal it, in whole
or in part, directly or indirectly to any person, entity, organization
or administration without the prior written consent of
Skyline Communications.

Any inquiries can be addressed to:

    Skyline Communications NV
    Ambachtenstraat 33
    B-8870 Izegem
    Belgium
    Tel.    : +32 51 31 35 69
    Fax.    : +32 51 31 01 29
    E-mail  : info@skyline.be
    Web     : www.skyline.be
    Contact : Ben Vandenberghe

****************************************************************************
Revision History:

DATE        VERSION     AUTHOR          COMMENTS

10/01/2023  1.0.0.1     BSM, Skyline    Initial Version

****************************************************************************
*/

namespace Script
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using Skyline.DataMiner.Automation;
    using Skyline.DataMiner.Core.DataMinerSystem.Automation;
    using Skyline.DataMiner.Core.DataMinerSystem.Common;
    using Skyline.DataMiner.DataMinerSolutions.ProcessAutomation.Helpers.Logging;
    using Skyline.DataMiner.DataMinerSolutions.ProcessAutomation.Manager;
    using Skyline.DataMiner.ExceptionHelper;
    using Skyline.DataMiner.Net.Apps.DataMinerObjectModel;
    using Skyline.DataMiner.Net.Sections;

    internal class Script
    {
        private DomHelper innerDomHelper;

        /// <summary>
        /// The Script entry point.
        /// </summary>
        /// <param name="engine">The <see cref="Engine" /> instance used to communicate with DataMiner.</param>
        public void Run(Engine engine)
        {
            engine.SetFlag(RunTimeFlags.NoCheckingSets);

            var scriptName = "Update Monitoring State";
            var tagElementName = "Pre-Code";
            var channelName = "Pre-Code";
            var helper = new PaProfileLoadDomHelper(engine);
            this.innerDomHelper = new DomHelper(engine.SendSLNetMessages, "process_automation");
            var exceptionHelper = new ExceptionHelper(engine, this.innerDomHelper);

            try
            {
                TagChannelInfo tagInfo = new TagChannelInfo(engine, helper, this.innerDomHelper);
                channelName = tagInfo.Channel;
                tagElementName = tagInfo.ElementName;
                engine.GenerateInformation("START " + scriptName);

                var filterColumn = new ColumnFilter { ComparisonOperator = ComparisonOperator.Equal, Value = tagInfo.ChannelMatch, Pid = 248 };
                var channelStatusRows = tagInfo.ChannelStatusTable.QueryData(new List<ColumnFilter> { filterColumn });
                if (channelStatusRows.Any())
                {
                    foreach (var row in channelStatusRows)
                    {
                        tagInfo.EngineElement.SetParameterByPrimaryKey(356, Convert.ToString(row[0]), (int)tagInfo.MonitorUpdate);
                    }
                }
                else
                {
                    var log = new Log
                    {
                        AffectedItem = scriptName,
                        AffectedService = channelName,
                        Timestamp = DateTime.Now,
                        ErrorCode = new ErrorCode
                        {
                            ConfigurationItem = channelName,
                            ConfigurationType = ErrorCode.ConfigType.Automation,
                            Source = scriptName,
                            Code = "ChannelNotFound",
                            Severity = ErrorCode.SeverityType.Warning,
                            Description = $"No channels found in channel status with given name: {channelName} in Channel Status Table.",
                        },
                    };

                    helper.Log($"No channels found in channel status with given name: {channelName}.", PaLogLevel.Error);
                    engine.GenerateInformation("Did not find any channels with match: " + tagInfo.ChannelMatch);
                    exceptionHelper.GenerateLog(log);
                }

                if (tagInfo.Status.Equals("deactivating"))
                {
                    helper.TransitionState("deactivating_to_complete");
                    engine.GenerateInformation("Successfully executed " + scriptName + " for: " + tagElementName);
                    helper.SendFinishMessageToTokenHandler();
                    return;
                }
                else if (tagInfo.Status.Equals("ready"))
                {
                    helper.TransitionState("ready_to_inprogress");
                }
                else if (tagInfo.Status.Equals("in_progress"))
                {
                    // no update
                }
                else
                {
                    var log = new Log
                    {
                        AffectedItem = scriptName,
                        AffectedService = channelName,
                        Timestamp = DateTime.Now,
                        ErrorCode = new ErrorCode
                        {
                            ConfigurationItem = channelName,
                            ConfigurationType = ErrorCode.ConfigType.Automation,
                            Source = scriptName,
                            Code = "InvalidStatusForTransition",
                            Severity = ErrorCode.SeverityType.Warning,
                            Description = $"Cannot execute the transition as the current status is unexpected. Current status: {tagInfo.Status}",
                        },
                    };

                    helper.Log($"Cannot execute the transition as the status. Current status: {tagInfo.ChannelMatch}", PaLogLevel.Error);
                    exceptionHelper.GenerateLog(log);
                }

                engine.GenerateInformation("Successfully executed " + scriptName + " for: " + tagElementName);
                helper.ReturnSuccess();
            }
            catch (Exception ex)
            {
                engine.GenerateInformation($"An issue occurred while executing {scriptName} activity for {channelName}: {ex}");
                var log = new Log
                {
                    AffectedItem = scriptName,
                    AffectedService = channelName,
                    Timestamp = DateTime.Now,
                    ErrorCode = new ErrorCode
                    {
                        ConfigurationItem = channelName,
                        ConfigurationType = ErrorCode.ConfigType.Automation,
                        Source = scriptName,
                        Severity = ErrorCode.SeverityType.Critical,
                        Description = "Exception while processing " + scriptName,
                    },
                };

                exceptionHelper.ProcessException(ex, log);
                helper.Log($"An issue occurred while executing {scriptName} activity for {channelName}: {ex}", PaLogLevel.Error);
                helper.SendErrorMessageToTokenHandler();
            }
        }

        /// <summary>
        /// Retry until success or until timeout.
        /// </summary>
        /// <param name="func">Operation to retry.</param>
        /// <param name="timeout">Max TimeSpan during which the operation specified in <paramref name="func"/> can be retried.</param>
        /// <returns><c>true</c> if one of the retries succeeded within the specified <paramref name="timeout"/>. Otherwise <c>false</c>.</returns>
        public static bool Retry(Func<bool> func, TimeSpan timeout)
        {
            bool success = false;

            Stopwatch sw = new Stopwatch();
            sw.Start();

            do
            {
                success = func();
                if (!success)
                {
                    Thread.Sleep(3000);
                }
            }
            while (!success && sw.Elapsed <= timeout);

            return success;
        }
    }

    public class TagChannelInfo
    {
        public string ElementName { get; set; }

        public string Channel { get; set; }

        public string ChannelMatch { get; set; }

        public string Threshold { get; set; }

        public string MonitoringMode { get; set; }

        public string Notification { get; set; }

        public string Encryption { get; set; }

        public string KMS { get; set; }

        public DomInstance Instance { get; set; }

        public Element EngineElement { get; set; }

        public IDmsElement Element { get; set; }

        public IDmsTable ChannelProfileTable { get; set; }

        public IDmsTable ChannelStatusTable { get; set; }

        public IDmsTable AllLayoutsTable { get; set; }

        public TagMonitoring MonitorUpdate { get; set; }

        public string Status { get; set; }

        public enum TagMonitoring
        {
            No = 0,
            Yes = 1,
        }

        public TagChannelInfo(Engine engine, PaProfileLoadDomHelper helper, DomHelper domHelper)
        {
            this.ElementName = helper.GetParameterValue<string>("TAG Element");
            this.Channel = helper.GetParameterValue<string>("Channel Name");
            this.ChannelMatch = helper.GetParameterValue<string>("Channel Match");

            IDms thisDms = engine.GetDms();
            this.Element = thisDms.GetElement(this.ElementName);
            this.EngineElement = engine.FindElement(this.Element.Name);
            this.ChannelProfileTable = this.Element.GetTable(8000);
            this.AllLayoutsTable = this.Element.GetTable(10300);
            this.ChannelStatusTable = this.Element.GetTable(240);

            this.MonitoringMode = helper.GetParameterValue<string>("Monitoring Mode");
            this.Threshold = helper.GetParameterValue<string>("Threshold");
            this.Notification = helper.GetParameterValue<string>("Notification");
            this.Encryption = helper.GetParameterValue<string>("Encryption");
            this.KMS = helper.GetParameterValue<string>("KMS");

            var instanceId = helper.GetParameterValue<string>("InstanceId");
            this.Instance = domHelper.DomInstances.Read(DomInstanceExposers.Id.Equal(new DomInstanceId(Guid.Parse(instanceId)))).First();
            this.Status = this.Instance.StatusId;

            this.MonitorUpdate = TagMonitoring.Yes;
            if (this.Status.Equals("deactivating"))
            {
                this.MonitorUpdate = TagMonitoring.No;
            }
        }
    }
}