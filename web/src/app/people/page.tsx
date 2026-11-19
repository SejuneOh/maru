"use client";

import { useEffect } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { Loading } from "@/components/Feedback";

/** The people board folded into the unified board; keep any filter in the URL. */
export default function PeopleRedirect() {
  const router = useRouter();
  const params = useSearchParams();
  const pathname = usePathname();
  useEffect(() => {
    if (pathname !== "/people") return;
    const qs = params.toString();
    router.replace(qs ? `/board?${qs}` : "/board");
  }, [router, params, pathname]);
  return <Loading />;
}
