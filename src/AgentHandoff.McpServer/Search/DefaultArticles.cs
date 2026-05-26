namespace AgentHandoff.McpServer.Search;

/// <summary>
/// Seed data uploaded to the Azure AI Search index on first start.
/// Bank customer-support knowledge base — branch hours, banking terms, common policies.
///
/// Articles include both English (generic retail bank) and Hebrew (Bank Discount Israel) content
/// so the BankingInfo agent can answer in the customer's language.
/// </summary>
internal static class DefaultArticles
{
    public static readonly KnowledgeBaseDocument[] All = new[]
    {
        // ═══════════════════════════════════════════════════════════════════
        //  ENGLISH ARTICLES — generic retail bank
        // ═══════════════════════════════════════════════════════════════════

        // ───────────────────────── BRANCH HOURS ─────────────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-branch-001", Topic = "branches",
            Title = "Downtown branch — opening hours",
            Content =
                "Downtown branch (123 Main St, Suite 100). Hours: Mon-Fri 9:00-17:00, " +
                "Sat 10:00-14:00, closed Sunday. Drive-up teller available Mon-Fri until 18:00. " +
                "Notary public on staff Tue and Thu afternoons. Wheelchair accessible. " +
                "Phone: (555) 123-0100.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-branch-002", Topic = "branches",
            Title = "Westside branch — opening hours",
            Content =
                "Westside branch (88 Park Ave). Hours: Mon-Fri 8:30-17:30, Sat 9:00-13:00, " +
                "closed Sunday. Extended Friday hours until 18:00. Safe-deposit boxes available. " +
                "Phone: (555) 123-0200.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-branch-003", Topic = "branches",
            Title = "Airport branch & ATM kiosk",
            Content =
                "Airport kiosk (Concourse C, Terminal 2): full-service teller Mon-Sun 6:00-22:00. " +
                "ATM is available 24/7. Currency exchange for USD ↔ EUR/GBP/JPY/CHF/CAD with " +
                "valid passport. Phone: (555) 123-0300.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-branch-004", Topic = "branches",
            Title = "Holiday schedule",
            Content =
                "All branches close on US federal banking holidays: New Year's Day, MLK Day, " +
                "Presidents' Day, Memorial Day, Juneteenth, Independence Day, Labor Day, " +
                "Columbus Day, Veterans Day, Thanksgiving, Christmas Day. " +
                "On the day before a holiday, branches close at 14:00. " +
                "Online banking and ATMs operate 24/7 regardless of holidays.",
        },

        // ──────────────────────── BANKING TERMS ─────────────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-term-apr",  Topic = "terms",
            Title = "APR vs APY — what's the difference?",
            Content =
                "APR (Annual Percentage Rate) is the simple yearly cost of borrowing — it does NOT " +
                "account for compounding within the year. Used on loans and credit cards. " +
                "APY (Annual Percentage Yield) IS the compounded yearly return — used on savings " +
                "and CDs. APY ≥ APR for the same nominal rate. Example: 5%% APR compounded monthly " +
                "yields 5.116%% APY.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-term-fdic", Topic = "terms",
            Title = "FDIC insurance coverage",
            Content =
                "Deposits at this bank are insured by the FDIC up to $250,000 per depositor, " +
                "per ownership category (single, joint, retirement, trust). Joint accounts " +
                "double the coverage to $500,000. Brokered CDs and money-market mutual funds " +
                "are NOT FDIC-insured. Coverage applies automatically — no application needed.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-term-iban", Topic = "terms",
            Title = "IBAN, SWIFT/BIC and routing numbers — when to use which",
            Content =
                "IBAN: International Bank Account Number — required for transfers TO accounts in " +
                "Europe, the Middle East, and parts of Asia. Format starts with a 2-letter country " +
                "code. SWIFT/BIC: 8- or 11-character bank identifier — required for international " +
                "wires regardless of region. Routing number (ABA): 9-digit US-only identifier for " +
                "domestic ACH and wire transfers. The US has no IBAN.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-term-overdraft", Topic = "terms",
            Title = "Overdraft and NSF fees",
            Content =
                "Overdraft fee is $35 per item, capped at 4 items per day ($140 max). NSF " +
                "(Non-Sufficient Funds) fee applies when an item is returned unpaid — also $35. " +
                "Customers can opt out of overdraft coverage at any time in online banking → " +
                "settings → overdraft preferences. Without coverage, debit-card transactions over " +
                "the available balance are simply declined (no fee).",
        },

        // ─────────────────── TRANSFERS & DIGITAL BANKING ────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-wire-001", Topic = "transfers",
            Title = "Wire transfers — domestic and international",
            Content =
                "Domestic wires post the same business day if submitted before 16:30 ET; fee $25. " +
                "International wires take 1-3 business days; fee $45 plus any correspondent-bank " +
                "fees. Provide the recipient's full legal name, account number / IBAN, and the " +
                "receiving bank's SWIFT/BIC. For currency conversion, the rate is locked at " +
                "submission time.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-ach-001",  Topic = "transfers",
            Title = "ACH transfers — timing and limits",
            Content =
                "Standard ACH transfers settle in 1-2 business days; same-day ACH (cutoff 13:45 ET) " +
                "settles by end of business. Daily transfer limit is $25,000 by default; can be " +
                "raised in branch with ID. ACH transfers are free for personal accounts.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-mobile-001", Topic = "digital",
            Title = "Mobile check deposit",
            Content =
                "Endorse the back of the check with 'For Mobile Deposit Only' and your " +
                "signature. Open the app → Deposit → photograph front and back. " +
                "Limits: $5,000 per check, $10,000 per day. Funds are available next " +
                "business day; first $225 is available immediately. Keep the original " +
                "check for 14 days, then shred.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-online-001", Topic = "digital",
            Title = "Online banking enrollment & password reset",
            Content =
                "Enroll at online.example-bank.com using your account number, SSN, and " +
                "email of record. Password reset: select 'Forgot password' on the sign-in " +
                "page; you'll receive a one-time code via SMS or email. If you no longer " +
                "have access to your registered phone or email, visit any branch with " +
                "government-issued ID.",
        },

        // ═══════════════════════════════════════════════════════════════════
        //  HEBREW ARTICLES — בנק דיסקונט (Bank Discount Israel)
        // ═══════════════════════════════════════════════════════════════════

        // ───────────────────── סניפים ושעות פעילות ─────────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-tlv", Topic = "branches",
            Title = "סניף תל אביב מרכז - שעות פתיחה",
            Content =
                "סניף תל אביב מרכז של בנק דיסקונט נמצא ברחוב יהודה הלוי 23, תל אביב. " +
                "שעות פעילות: ימים א׳, ב׳, ד׳, ה׳ 08:30-13:30. יום ג׳ 08:30-13:30 ובנוסף 16:00-18:30. " +
                "יום ו׳ וערבי חג 08:30-12:30. שבת וחגים סגור. " +
                "שירותי מט״ח, אשראי, וקופה זמינים בכל ימי הפעילות. " +
                "טלפון: 03-5141111. דרכי הגעה: רכבת קלה, תחנת אלנבי.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-jrm", Topic = "branches",
            Title = "סניף ירושלים מרכז העיר - שעות פתיחה",
            Content =
                "סניף ירושלים מרכז העיר של בנק דיסקונט נמצא ברחוב יפו 45, ירושלים. " +
                "שעות פעילות: ימים א׳-ה׳ 08:30-13:30. יום ג׳ שעות נוספות 16:00-18:30. " +
                "יום ו׳ סגור (סניף מתכבד). שבת וחגים סגור. " +
                "פגישת ייעוץ אישי בתיאום מראש דרך אתר הבנק או באפליקציה. " +
                "טלפון: 02-6708000.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-haifa", Topic = "branches",
            Title = "סניף חיפה הדר - שעות פתיחה",
            Content =
                "סניף חיפה הדר של בנק דיסקונט ברחוב הנביאים 12, חיפה. " +
                "שעות פעילות: ימים א׳, ב׳, ד׳, ה׳ 08:30-13:30. יום ג׳ 08:30-13:30 ובנוסף 16:00-18:30. " +
                "יום ו׳ וערבי חג 08:30-12:30. שבת וחגים סגור. " +
                "כספומט פנים זמין 24/7 בכניסה לסניף.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-holidays", Topic = "branches",
            Title = "לוח חגים ושעות חירום",
            Content =
                "כל סניפי בנק דיסקונט סגורים בחגי ישראל הרשמיים: ראש השנה, יום כיפור, " +
                "סוכות (חג ראשון ושמיני עצרת), פסח (חג ראשון ושביעי), יום העצמאות, ושבועות. " +
                "בערב חג הסניפים סוגרים בשעה 12:00 ושירות מקוצר בלבד. " +
                "אפליקציית דיסקונט, אתר האינטרנט הבנקאי, ומכשירי כספומט פעילים 24/7 כולל בחגים. " +
                "מוקד שירות הלקוחות פעיל בימי חול 24/6 בטלפון *6111.",
        },

        // ────────────────────── מונחים בנקאיים ─────────────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-prime", Topic = "terms",
            Title = "ריבית פריים - מהי?",
            Content =
                "ריבית הפריים היא ריבית הבסיס שקובע בנק ישראל בתוספת 1.5%. " +
                "נכון לתחילת 2026 הפריים עומד על 6.0% (ריבית בנק ישראל 4.5% + 1.5%). " +
                "ריביות הלוואה רבות נקובות כ׳פריים פלוס׳ - לדוגמה ׳פריים + 1.5%׳ פירושו ריבית של 7.5%. " +
                "כאשר בנק ישראל משנה את הריבית, ריביות אלו משתנות בהתאם.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-fixed-vs-var", Topic = "terms",
            Title = "ריבית קבועה מול ריבית משתנה",
            Content =
                "ריבית קבועה אינה משתנה במהלך תקופת ההלוואה - יציבות בתשלום החודשי לאורך כל הדרך. " +
                "ריבית משתנה צמודה לבסיס כגון פריים, מק״מ, או מדד - יכולה לעלות או לרדת לאורך התקופה. " +
                "במשכנתא ארוכת טווח, רוב הלקוחות מפצלים בין מסלולים: חלק קבוע (לביטחון) וחלק משתנה (לחסכון פוטנציאלי). " +
                "דיסקונט מציע מחשבון משכנתא באתר הבנק להשוואת מסלולים.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-cpi", Topic = "terms",
            Title = "הצמדה למדד המחירים לצרכן",
            Content =
                "הצמדה למדד המחירים לצרכן (מדד) משמעה שיתרת ההלוואה או הפיקדון משתנה ביחס לאינפלציה. " +
                "בהלוואה צמודה - היתרה גדלה בעת אינפלציה (החזר אמיתי גבוה יותר). " +
                "בפיקדון צמוד - ערך הפיקדון מוגן מפני שחיקת האינפלציה. " +
                "מסלול ׳צמוד מדד עם ריבית קבועה׳ = יציבות אבל חשיפה לעליות מדד. " +
                "מסלול ׳לא צמוד׳ = ההיפך - חשיפה לאינפלציה אבל ריבית בדרך כלל גבוהה יותר.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-fdic-il", Topic = "terms",
            Title = "ביטוח פיקדונות בישראל",
            Content =
                "אין בישראל הגנה ממשלתית רשמית על פיקדונות בנקאיים בסכומים מוגדרים כמו ה-FDIC בארה״ב. " +
                "עם זאת, בנק דיסקונט נמצא תחת פיקוח הבנקים של בנק ישראל ומחויב ביחסי הון מחמירים על פי באזל III. " +
                "הבנק הוכרז בעבר כ׳בנק חיוני לכלכלה הישראלית׳, מה שמעניק שכבת ביטחון נוספת. " +
                "פיקדונות באג״ח מדינה נחשבים בטוחים פעולתית.",
        },

        // ─────────────────────── העברות ופעולות ─────────────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-transfer-il", Topic = "transfers",
            Title = "העברות בנקאיות בארץ - סוגים ועלויות",
            Content =
                "העברה רגילה (זה״ב - זיכוי במקטעי בנקים): מתבצעת תוך 1-3 ימי עסקים. עמלה: 1.65 ש״ח. " +
                "העברה מיידית (Faster Payments): תוך שעה בודדה בשעות העבודה. עמלה: 4.50 ש״ח. סכום מקסימלי: 100,000 ש״ח. " +
                "העברת מסיב/RTGS: מיידית, מתאים לסכומים גבוהים. עמלה: כ-30 ש״ח. " +
                "ניתן לבצע דרך אפליקציית דיסקונט, אתר האינטרנט הבנקאי, או בסניף.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-swift", Topic = "transfers",
            Title = "העברות בנקאיות לחו״ל (SWIFT)",
            Content =
                "העברת SWIFT לחו״ל מבנק דיסקונט מתבצעת תוך 1-3 ימי עסקים. " +
                "עמלת בנק שולח: 30-80 ש״ח לפי תעריף הסניף. " +
                "עמלת בנקים מתווכים (correspondent fees): עשויה להוסיף 15-50$ לפי המסלול. " +
                "פרטים נדרשים: שם מלא של המוטב, IBAN או מספר חשבון, שם הבנק המקבל, קוד SWIFT/BIC, וכתובת המוטב. " +
                "המרת מטבע: בשער יום בנקאי של דיסקונט, עם מרווח של כ-0.5%-1.5% מעל השער המייצג של בנק ישראל.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-fx", Topic = "transfers",
            Title = "שערי חליפין במטבע חוץ",
            Content =
                "שערי חליפין נקבעים יומית לפי שערי בנק דיסקונט. השער המייצג של בנק ישראל מתפרסם בערך הראשון של היום. " +
                "עבור עסקאות בסכומים גבוהים מ-50,000 ש״ח, ניתן לפנות ליועץ מט״ח לקבלת שער מועדף (Negotiated rate). " +
                "עמלת המרה: 0.4%-0.7% משווי העסקה לפי הצעדה. " +
                "המרה במזומן בסניף: זמינה למטבעות עיקריים (USD, EUR, GBP, CHF) עד 5,000 ש״ח שווי. סכום גבוה יותר - בתיאום מראש.",
        },

        // ─────────────────────── דיגיטל ושירותים ────────────────────────
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-app", Topic = "digital",
            Title = "אפליקציית דיסקונט - שירותים זמינים",
            Content =
                "אפליקציית דיסקונט זמינה ל-iOS וגם ל-Android בחנויות הרשמיות. " +
                "שירותים זמינים: צפייה ביתרות וחשבונות, העברות בארץ ולחו״ל, הפקדת צ׳קים בצילום, " +
                "הזמנת כרטיס אשראי, פתיחת פיקדון, צפייה בדפי חשבון היסטוריים, פגישה דיגיטלית עם בנקאי, " +
                "חיפוש סניף וכספומט, ועדכון פרטים אישיים. " +
                "כניסה: באמצעות זיהוי ביומטרי (טביעת אצבע, Face ID) או קוד אישי בן 6 ספרות. " +
                "ההורדה והשימוש חינם. גרסה מינימלית: iOS 14, Android 8.",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-cards", Topic = "digital",
            Title = "כרטיסי אשראי דיסקונט - סוגים ותנאים",
            Content =
                "דיסקונט מציע מגוון כרטיסי אשראי בשיתוף עם חברות הסליקה: " +
                "1) דיסקונט קלאסיק - עמלה חודשית 11.90 ש״ח, מתאים לשימוש כללי. " +
                "2) דיסקונט גולד - עמלה 21.90 ש״ח, כולל ביטוח נסיעות וגישה ללאונג׳ים. " +
                "3) דיסקונט פלטינום - עמלה 79 ש״ח, ללא הגבלת מסגרת ושירותי קונסיירז׳. " +
                "4) דיסקונט סטודנט - עמלה 0 ש״ח עד גיל 30, מסגרת בסיסית 5,000 ש״ח. " +
                "החזר עמלה אפשרי בהוצאה חודשית מעל 3,000 ש״ח (לרוב הכרטיסים).",
        },
        new KnowledgeBaseDocument
        {
            Id = "kb-bd-loans", Topic = "digital",
            Title = "הלוואות אישיות ומשכנתאות בדיסקונט",
            Content =
                "הלוואה אישית: סכום עד 250,000 ש״ח, ריבית מ-פריים+0.5% עד פריים+5% לפי הסיכון. " +
                "תקופת החזר: 12-84 חודשים. אישור מהיר באפליקציה לכל לקוח קיים תוך 24 שעות. " +
                "משכנתא: עד 75% מערך הנכס לרוכש דירה ראשונה (קבוצה א׳), עד 50% לקבוצה ב׳ (משקיעים). " +
                "תקופת המשכנתא: עד 30 שנה. תהליך אישור עקרוני: 7-21 ימי עסקים. " +
                "מסמכים נדרשים: 3 תלושי שכר אחרונים, אישור מעסיק, תדפיס בנק 6 חודשים, אישור על נכסים והתחייבויות.",
        },
    };
}
