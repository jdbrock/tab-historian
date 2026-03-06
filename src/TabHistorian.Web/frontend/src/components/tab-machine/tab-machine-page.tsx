"use client";

import { useState } from "react";
import Link from "next/link";

import { History } from "lucide-react";
import { useTabMachineStats } from "@/lib/hooks";
import { formatTimestamp } from "@/lib/utils";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { TabSearch } from "./tab-search";
import { TimeTravel } from "./time-travel";

export function TabMachinePage() {
  const { data: stats } = useTabMachineStats();
  const [tab, setTab] = useState(() => {
    if (typeof window === "undefined") return "search";
    try { return localStorage.getItem("th-tab") ?? "search"; } catch { return "search"; }
  });

  return (
    <div className="min-h-screen flex flex-col">
      <div className="flex-1 flex flex-col items-center px-4 pt-2 sm:pt-4 pb-8">
        <div className="mb-2 text-center">
          <img src="/logo.png" alt="Tab Historian" className="mx-auto w-56" />
          {stats && (
            <div className="flex items-center gap-3 mt-2 text-xs text-muted-foreground justify-center">
              <span>{stats.totalTabs.toLocaleString()} tabs tracked</span>
              <span>{stats.openTabs.toLocaleString()} open</span>
              <span>{stats.totalEvents.toLocaleString()} events</span>
              {stats.lastSeen && (
                <span>last seen {formatTimestamp(stats.lastSeen)}</span>
              )}
            </div>
          )}
          <Link
            href="/snapshots"
            className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground transition-colors mt-2"
          >
            <History className="h-3 w-3" />
            Full Snapshots
          </Link>
        </div>

        <div className="w-full max-w-3xl">
          <Tabs value={tab} onValueChange={(v) => { setTab(v); try { localStorage.setItem("th-tab", v); } catch {} }}>
            <TabsList className="w-full justify-center">
              <TabsTrigger value="search">Search</TabsTrigger>
              <TabsTrigger value="timeline">Time Travel</TabsTrigger>
            </TabsList>
            <TabsContent value="search" className="mt-4">
              <TabSearch />
            </TabsContent>
            <TabsContent value="timeline" className="mt-4">
              <TimeTravel />
            </TabsContent>
          </Tabs>
        </div>
      </div>
    </div>
  );
}
