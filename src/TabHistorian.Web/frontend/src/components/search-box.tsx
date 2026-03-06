"use client";

import { useEffect, useState } from "react";
import { Search, X } from "lucide-react";
import { Input } from "@/components/ui/input";

interface SearchBoxProps {
  onQueryChange: (query: string) => void;
  debounceMs?: number;
}

export function SearchBox({ onQueryChange, debounceMs = 100 }: SearchBoxProps) {
  const [value, setValue] = useState("");

  useEffect(() => {
    const timer = setTimeout(() => onQueryChange(value), debounceMs);
    return () => clearTimeout(timer);
  }, [value, debounceMs, onQueryChange]);

  return (
    <div className="relative w-full">
      <Search className="absolute left-4 top-1/2 -translate-y-1/2 h-5 w-5 text-muted-foreground" />
      <Input
        type="text"
        placeholder="Search tabs, URLs, history..."
        value={value}
        onChange={(e) => setValue(e.target.value)}
        className="pl-12 pr-10 py-6 text-lg rounded-xl bg-card border-border focus-visible:ring-2 focus-visible:ring-ring"
      />
      {value && (
        <button
          onClick={() => setValue("")}
          className="absolute right-4 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
        >
          <X className="h-4 w-4" />
        </button>
      )}
    </div>
  );
}
