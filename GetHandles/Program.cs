﻿//  Copyright 2015 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http ://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using HandleUtils;
using NDesk.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TokenLibrary;

namespace GetHandles
{
    class Program
    {
        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: GetHandles [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        enum GroupingMode
        {
            Pid,
            Object,
            Name,
            Type,            
        }
        
        enum ShareMode
        {
            None,
            Partial,
            All,
        }

        private static void PrintGrouping<T>(IEnumerable<IGrouping<T, HandleEntry>> grouping, Dictionary<int, string> pidToName, 
            Func<T, string> formatHeader, Func<HandleEntry, string> formatHandle, ShareMode shareMode, int pidCount, bool showsd)
        {
            foreach (var group in grouping)
            {
                if (shareMode == ShareMode.All)
                {
                    if (group.GroupBy(k => k.ProcessId).Count() != pidCount)
                    {
                        continue;
                    }
                }
                else if (shareMode == ShareMode.Partial)
                {
                    if (group.GroupBy(k => k.ProcessId).Count() <= 1)
                    {
                        continue;
                    }
                }

                Console.WriteLine(formatHeader(group.Key));

                foreach (HandleEntry ent in group)
                {
                    Console.WriteLine("{0}/0x{0:X}/{1} {2}/0x{2:X}: {3}", ent.ProcessId, pidToName[ent.ProcessId], 
                        ent.Handle.ToInt32(), formatHandle(ent));
                    if (showsd && !String.IsNullOrWhiteSpace(ent.StringSecurityDescriptor))
                    {
                        Console.WriteLine("SDDL: {0}", ent.StringSecurityDescriptor);
                    }
                }
                Console.WriteLine();
            }
        }

        static void Main(string[] args)
        {
            try
            {
                bool show_help = false;
                HashSet<string> typeFilter = new HashSet<string>();
                HashSet<int> pidFilter = new HashSet<int>();
                HashSet<string> nameFilter = new HashSet<string>();
                bool noquery = false;
                GroupingMode mode = GroupingMode.Pid;
                ShareMode shareMode = ShareMode.None;
                bool showsd = false;

                OptionSet opts = new OptionSet() {
                        { "t|type=", "An object type to filter on, can be repeated",  v => typeFilter.Add(v.Trim().ToLower()) },
                        { "p|pid=", "A PID to filter on, can be repeated", v => pidFilter.Add(int.Parse(v.Trim())) },
                        { "n|name=", "Specify a process by name", v => nameFilter.Add(v.ToLower()) },
                        { "q|noquery", "Don't query for names/typenames", v => noquery = v != null },
                        { "g|group=", "Specify a grouping, defaults to pid, can be object,name,type", v => mode = (GroupingMode)Enum.Parse(typeof(GroupingMode), v, true) },
                        { "s|share=", "When grouping, filter on shared, can be none,partial or all", v => shareMode = (ShareMode)Enum.Parse(typeof(ShareMode), v, true) },
                        { "sd", "Display the security descriptor associated with the kernel object", v => showsd = v != null },
                        { "h|help",  "show this message and exit", 
                           v => show_help = v != null },
                    };

                opts.Parse(args);

                if (show_help)
                {
                    ShowHelp(opts);
                }
                else
                {
                    List<ProcessEntry> pss = ProcessEntry.GetProcesses();

                    IEnumerable<ProcessEntry> filtered = pss;

                    if (pidFilter.Count > 0)
                    {
                        filtered = filtered.Where(ps => pidFilter.Contains(ps.Pid));
                    }

                    if (nameFilter.Count > 0)
                    {
                        filtered = filtered.Where(ps => nameFilter.Contains(ps.Name, StringComparer.OrdinalIgnoreCase));
                    }
                    
                    HashSet<int> pids = new HashSet<int>(filtered.Select(process => process.Pid));
                    Dictionary<int, string> pidToName = filtered.ToDictionary(pk => pk.Pid, pv => pv.Name);                    

                    List<HandleEntry> totalHandles = new List<HandleEntry>();

                    foreach (int pid in pids)
                    {
                        if (pid == Process.GetCurrentProcess().Id)
                        {
                            continue;
                        }

                        IEnumerable<HandleEntry> handles = NativeBridge.GetHandlesForPid(pid, noquery).Where(ent => (typeFilter.Count == 0) || typeFilter.Contains(ent.TypeName.ToLower()));
                        totalHandles.AddRange(handles);
                        if (mode == GroupingMode.Pid)
                        {
                            Console.WriteLine("Process ID: {0} - Name: {1}", pid, pidToName[pid]);
                            foreach (HandleEntry ent in handles)
                            {                                
                                Console.WriteLine("{0:X04}: {1:X016} {2:X08} {3,20} {4}", ent.Handle.ToInt32(), ent.Object.ToInt64(), ent.GrantedAccess, ent.TypeName, ent.ObjectName);
                                if (showsd && !String.IsNullOrWhiteSpace(ent.StringSecurityDescriptor))
                                {
                                    Console.WriteLine("SDDL: {0}", ent.StringSecurityDescriptor);
                                }
                            }
                            Console.WriteLine();
                        } 
                    }

                    switch(mode)
                    {
                        case GroupingMode.Type:
                            PrintGrouping(totalHandles.GroupBy(f => f.TypeName), pidToName, k => String.Format("Type: {0}", k), 
                                e => String.Format("{0:X08} {1:X08} {2}", e.Object.ToInt64(), e.GrantedAccess, e.ObjectName), 
                                shareMode, pids.Count, showsd);
                            break;
                        case GroupingMode.Object:
                            PrintGrouping(totalHandles.GroupBy(f => f.Object), pidToName, k => String.Format("Object: {0:X08}", k.ToInt64()),
                                e => String.Format("{0,20} {1:X08} {2}", e.TypeName, e.GrantedAccess, e.ObjectName),
                                shareMode, pids.Count, showsd);
                            break;
                        case GroupingMode.Name:
                            PrintGrouping(totalHandles.GroupBy(f => f.ObjectName), pidToName, k => String.Format("Name: {0:X08}", k),
                                e => String.Format("{0:X08} {1,20} {2:X08} {2}", e.Object.ToInt64(), e.TypeName, e.GrantedAccess), 
                                shareMode, pids.Count, showsd);
                            break;                        
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
