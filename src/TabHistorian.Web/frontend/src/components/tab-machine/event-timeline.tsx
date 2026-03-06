"use client";

import { ExternalLink } from "lucide-react";
import { useTabEvents } from "@/lib/hooks";
import { faviconUrl, formatTimestamp, cleanTitle } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import type { TabIdentity } from "@/lib/types";

const hiddenFields = new Set([
  "navigationHistory", "syncTabNodeId", "tabGroupToken",
  "extensionAppId", "windowType", "showState",
  "url", "title", "profileName", "profileDisplayName",
]);

const fieldLabels: Record<string, string> = {
  pinned: "Pinned",
  isActive: "Active",
  lastActiveTime: "Last Active",
  windowIndex: "Window",
  tabIndex: "Position",
};

function formatValue(key: string, val: unknown): string {
  if (val === null || val === undefined) return "\u2014";
  if (typeof val === "boolean") return val ? "Yes" : "No";
  if (key === "lastActiveTime" && typeof val === "string") return formatTimestamp(val);
  if (typeof val === "string") return val.length > 120 ? val.slice(0, 120) + "\u2026" : val;
  return String(val);
}

function StateDelta({ raw }: { raw: string }) {
  let parsed: Record<string, unknown>;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  const title = typeof parsed.title === "string" ? parsed.title : null;
  const url = typeof parsed.url === "string" ? parsed.url : null;

  const extraEntries = Object.entries(parsed).filter(([key]) => !hiddenFields.has(key));

  return (
    <div className="mt-1.5">
      {(title || url) && (
        <div className="flex items-center gap-2 min-w-0">
          {url && (
            /* eslint-disable-next-line @next/next/no-img-element */
            <img
              src={faviconUrl(url)}
              alt=""
              width={14}
              height={14}
              className="rounded-sm shrink-0"
            />
          )}
          <span className="text-sm truncate">
            {title ? cleanTitle(title) : url}
          </span>
          {url && (
            <a
              href={url}
              target="_blank"
              rel="noopener noreferrer"
              onClick={(e) => e.stopPropagation()}
              className="text-muted-foreground hover:text-foreground shrink-0"
            >
              <ExternalLink className="h-3.5 w-3.5" />
            </a>
          )}
        </div>
      )}
      {url && (
        <p className="text-xs text-muted-foreground/60 truncate mt-0.5 pl-[22px]">
          {url}
        </p>
      )}
      {extraEntries.length > 0 && (
        <div className="mt-1.5 space-y-0.5 text-xs pl-[22px]">
          {extraEntries.map(([key, val]) => (
            <div key={key} className="flex gap-2">
              <span className="text-muted-foreground/70 shrink-0 w-20">
                {fieldLabels[key] ?? key.replace(/([A-Z])/g, " $1").replace(/^./, s => s.toUpperCase()).trim()}
              </span>
              <span className="text-muted-foreground break-all">
                {formatValue(key, val)}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

const eventColors: Record<string, string> = {
  Opened: "bg-green-500/20 text-green-400",
  Closed: "bg-red-500/20 text-red-400",
  Navigated: "bg-blue-500/20 text-blue-400",
  TitleChanged: "bg-yellow-500/20 text-yellow-400",
  Pinned: "bg-purple-500/20 text-purple-400",
  Unpinned: "bg-purple-500/20 text-purple-400",
  Updated: "bg-zinc-500/20 text-zinc-400",
};

interface EventTimelineProps {
  identity: TabIdentity;
}

export function EventTimeline({ identity }: EventTimelineProps) {
  const { data, isLoading } = useTabEvents({
    tabIdentityId: identity.id,
    pageSize: 100,
  });

  if (isLoading) {
    return (
      <div className="space-y-3 p-4">
        {Array.from({ length: 3 }).map((_, i) => (
          <Skeleton key={i} className="h-16 w-full" />
        ))}
      </div>
    );
  }

  const events = data?.items ?? [];

  if (events.length === 0) {
    return (
      <p className="text-sm text-muted-foreground p-4">No events recorded.</p>
    );
  }

  return (
    <div className="space-y-1">
      <div className="px-4 pt-3 pb-1">
        <h4 className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          Events
        </h4>
      </div>
      {events.map((event) => (
        <div
          key={event.id}
          className="flex items-start gap-3 px-4 py-2 hover:bg-accent/30 rounded-md"
        >
          <div className="mt-0.5 w-2 h-2 rounded-full bg-muted-foreground shrink-0" />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <Badge
                className={`text-xs font-normal ${eventColors[event.eventType] ?? ""}`}
              >
                {event.eventType}
              </Badge>
              <span className="text-xs text-muted-foreground">
                {formatTimestamp(event.timestamp)}
              </span>
            </div>
            {event.stateDelta && (
              <StateDelta raw={event.stateDelta} />
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
