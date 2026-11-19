"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { Loading } from "@/components/Feedback";
import { useMe } from "@/lib/me";

/** My work folded into the unified board: land on /board filtered to the signed-in person. */
export default function MyWorkRedirect() {
  const router = useRouter();
  const login = useMe().data?.member.githubLogin;
  useEffect(() => {
    router.replace(login ? `/board?person=${encodeURIComponent(login)}` : "/board");
  }, [router, login]);
  return <Loading />;
}
