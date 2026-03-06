"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { LayoutGrid, List } from "lucide-react";
import { useTabMachineSearch, useTabMachineProfiles } from "@/lib/hooks";
import { SearchBox } from "@/components/search-box";
import { TabIdentityCard } from "./tab-identity-card";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { TabIdentity } from "@/lib/types";

type ViewMode = "window" | "flat";

function loadStoredFilter<T>(key: string, parse: (v: string) => T, fallback: T): T {
  if (typeof window === "undefined") return fallback;
  try {
    const v = localStorage.getItem(key);
    return v !== null ? parse(v) : fallback;
  } catch { return fallback; }
}

function storeFilter(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch { /* ignore */ }
}

export function TabSearch() {
  const [query, setQuery] = useState("");
  const [profile, setProfile] = useState<string | undefined>(() =>
    loadStoredFilter("th-profile", (v) => v === "all" ? undefined : v, undefined)
  );
  const [isOpen, setIsOpen] = useState<boolean | undefined>(() =>
    loadStoredFilter("th-isOpen", (v) => v === "all" ? undefined : v === "open", true)
  );
  const [viewMode, setViewMode] = useState<ViewMode>(() =>
    loadStoredFilter("th-viewMode", (v) => (v === "window" ? "window" : "flat") as ViewMode, "flat")
  );

  const { data: profiles } = useTabMachineProfiles();

  const effectiveSort = viewMode === "window" ? "window" : undefined;

  const { data, isLoading, hasNextPage, fetchNextPage, isFetchingNextPage } =
    useTabMachineSearch({
      q: query || undefined,
      profile,
      isOpen,
      sort: effectiveSort,
      pageSize: viewMode === "window" ? 200 : 50,
    });

  const handleQueryChange = useCallback((q: string) => setQuery(q), []);

  // Infinite scroll sentinel
  const sentinelRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!sentinelRef.current) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: "200px" }
    );
    observer.observe(sentinelRef.current);
    return () => observer.disconnect();
  }, [hasNextPage, fetchNextPage, isFetchingNextPage]);

  const allItems = data?.pages.flatMap((p) => p.items) ?? [];
  const totalCount = data?.pages[0]?.totalCount ?? 0;

  return (
    <div className="space-y-4">
      <SearchBox onQueryChange={handleQueryChange} />

      <div className="flex items-center gap-3 flex-wrap">
        {profiles && profiles.length > 1 && (
          <Select
            value={profile ?? "all"}
            onValueChange={(v) => {
              setProfile(v === "all" ? undefined : v);
              storeFilter("th-profile", v);
            }}
          >
            <SelectTrigger size="sm" className="w-auto">
              <SelectValue placeholder="All profiles" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All profiles</SelectItem>
              {profiles.map((p) => (
                <SelectItem key={p.profileName} value={p.profileName}>
                  {p.displayName ?? p.profileName}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <Select
          value={isOpen === undefined ? "all" : isOpen ? "open" : "closed"}
          onValueChange={(v) => {
            const val = v === "all" ? undefined : v === "open";
            setIsOpen(val);
            storeFilter("th-isOpen", v);
          }}
        >
          <SelectTrigger size="sm" className="w-auto">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All tabs</SelectItem>
            <SelectItem value="open">Open</SelectItem>
            <SelectItem value="closed">Closed</SelectItem>
          </SelectContent>
        </Select>
        <div className="flex items-center rounded-md border border-border/50 ml-auto">
          <Button
            variant={viewMode === "flat" ? "secondary" : "ghost"}
            size="sm"
            className="h-8 rounded-r-none gap-1.5 px-3"
            onClick={() => { setViewMode("flat"); storeFilter("th-viewMode", "flat"); }}
          >
            <List className="h-3.5 w-3.5" />
            Tabs
          </Button>
          <Button
            variant={viewMode === "window" ? "secondary" : "ghost"}
            size="sm"
            className="h-8 rounded-l-none gap-1.5 px-3"
            onClick={() => { setViewMode("window"); storeFilter("th-viewMode", "window"); }}
          >
            <LayoutGrid className="h-3.5 w-3.5" />
            Windows
          </Button>
        </div>
      </div>

      <div className="space-y-2">
        {isLoading ? (
          Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-24 w-full" />
          ))
        ) : viewMode === "window" ? (
          <WindowGroupedFeed items={allItems} />
        ) : (
          allItems.map((identity) => (
            <TabIdentityCard key={identity.id} identity={identity} />
          ))
        )}
        {allItems.length === 0 && !isLoading && (
          <p className="text-sm text-muted-foreground text-center py-8">
            No tabs found.
          </p>
        )}
        <div ref={sentinelRef} />
        {isFetchingNextPage && <Skeleton className="h-24 w-full" />}
      </div>
    </div>
  );
}

function WindowGroupedFeed({ items }: { items: TabIdentity[] }) {
  const groups: { key: string; profile: string; windowIndex: number; tabs: TabIdentity[] }[] = [];

  for (const item of items) {
    const key = `${item.profileName}:${item.windowIndex}`;
    const last = groups[groups.length - 1];
    if (last && last.key === key) {
      last.tabs.push(item);
    } else {
      groups.push({
        key,
        profile: item.profileDisplayName ?? item.profileName,
        windowIndex: item.windowIndex,
        tabs: [item],
      });
    }
  }

  return (
    <div className="space-y-4">
      {groups.map((group) => (
        <div key={group.key}>
          <div className="flex items-center gap-2 mb-1.5 px-1">
            <h3 className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
              {group.profile}
            </h3>
            <span className="text-xs text-muted-foreground/60">
              Window {group.windowIndex + 1}
            </span>
            <span className="text-xs text-muted-foreground/40">
              {group.tabs.length} tab{group.tabs.length !== 1 ? "s" : ""}
            </span>
          </div>
          <div className="space-y-1.5">
            {group.tabs.map((identity) => (
              <TabIdentityCard key={identity.id} identity={identity} />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
