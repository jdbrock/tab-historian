"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, useEffect, type ReactNode } from "react";

export function QueryProvider({ children }: { children: ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30_000,
            refetchOnWindowFocus: false,
          },
        },
      })
  );

  useEffect(() => {
    const es = new EventSource("/api/events");
    es.onmessage = (e) => {
      try {
        const data = JSON.parse(e.data);
        if (data.type === "db-updated") {
          client.invalidateQueries();
        }
      } catch {
        // ignore non-JSON messages (e.g. "connected")
      }
    };
    return () => es.close();
  }, [client]);

  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
