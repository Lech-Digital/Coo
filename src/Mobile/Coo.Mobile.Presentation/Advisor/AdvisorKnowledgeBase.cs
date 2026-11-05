namespace Coo.Mobile.Presentation.Advisor;

public sealed record DevelopmentStageInfo(
    string StageBadge,
    string Summary,
    string MotorSkills,
    string CognitiveSensory,
    string SocialSpeech,
    string RhythmSleep,
    string PediatricTip);

public static class AdvisorKnowledgeBase
{
    public static DevelopmentStageInfo GetStage(int daysOld)
    {
        if (daysOld < 14)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 0–2 Weeks · Newborn Stage",
                Summary: "Your baby is adjusting to life outside the womb. Movement is guided by newborn reflexes and sensory exploration of parents' voices and touch.",
                MotorSkills: "• Strong grasp reflex (clasps fingers tightly)\n• Startles (Moro reflex) to loud sounds or sudden shifts\n• Turns head side-to-side when resting on tummy\n• Keeps arms and legs flexed close to the body in fists",
                CognitiveSensory: "• Sees best at 8–12 inches (the distance to your face while feeding)\n• Drawn to high-contrast black-and-white shapes and faces\n• Recognizes mother's and father's voices heard in the womb\n• Sensitive to smells, especially breastmilk and mother's skin",
                SocialSpeech: "• Calms to gentle rocking, swaddling, and shushing sounds\n• Reflexive smiles during REM sleep\n• Communicates hunger, fatigue, and discomfort primarily by crying",
                RhythmSleep: "• Sleeps 16–18 hours across 24h in short 1.5–3 hour bursts\n• Feeds every 2–3 hours (8–12 times per day)\n• Wake windows are very short: 30–45 minutes max",
                PediatricTip: "Skin-to-skin contact ('kangaroo care') stabilizes baby's body temperature, blood sugar, and heartbeat while promoting bonding."
            );
        }

        if (daysOld < 45)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 2–6 Weeks · Awakening & Growth",
                Summary: "Baby becomes noticeably more alert. Growth spurts typically occur at 3 and 6 weeks, accompanied by cluster feeding and longer awake periods.",
                MotorSkills: "• Lifts head briefly (45°) when placed on tummy\n• Neck muscles strengthen; less floppy when supported\n• Hands open more frequently from tight fists\n• Smooth, reciprocal kicking of legs when lying on back",
                CognitiveSensory: "• Tracks faces and moving objects briefly with eyes\n• Shows sustained interest in bright colors and bold patterns\n• Turns head purposefully toward familiar voices\n• Quiets down when hearing soothing classical music or white noise",
                SocialSpeech: "• First intentional social smiles emerge (usually around 4–6 weeks)!\n• Begins making soft vowel gurgles ('ah', 'oh', 'eh')\n• Makes sustained, meaningful eye contact during calm alert states",
                RhythmSleep: "• Total sleep: 15–17 hours daily; wake windows: 45–60 minutes\n• Fussy 'witching hours' often peak in the evening\n• Cluster feeding is very common during growth spurts",
                PediatricTip: "Start doing 2–3 short bursts of tummy time (2–3 minutes each) daily on a firm mat or chest-to-chest."
            );
        }

        if (daysOld < 90)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 6–12 Weeks · Social Smiles & Coos",
                Summary: "A delightful, highly social stage! Your baby responds to your voice with joyful coos, big grins, and active arm batting.",
                MotorSkills: "• Holds head steady at 45–90° during tummy time, bearing weight on forearms\n• Brings both hands to mouth and plays with fingers\n• Swats and bats at dangling toys with uncoordinated swipes\n• Pushes down against your lap when feet touch a flat surface",
                CognitiveSensory: "• Tracks toys and faces smoothly across 180° midline\n• Studies their own hands with wonder and fascination\n• Distinguishes between familiar family members and strangers\n• Reacts happily to smiling expressions and lively voices",
                SocialSpeech: "• Delights in vocal play: long vowel coos ('ooo', 'aaa', 'eee')\n• Engages in 'conversations' by waiting for you to reply before cooing again\n• Chuckles and smiles broadly in response to peek-a-boo and tickles",
                RhythmSleep: "• Total sleep: 14–16 hours; wake windows: 60–90 minutes\n• Night sleep begins stretching into 4–6 hour uninterrupted blocks\n• Daytime shifts toward 4–5 defined naps",
                PediatricTip: "Establish a consistent 15-minute bedtime routine: bath, gentle massage, diaper, feed, and lullaby in dim light."
            );
        }

        if (daysOld < 150)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 3–5 Months · Rolling & Discovery",
                Summary: "Babies are increasingly mobile, curious, and playful. Sleep patterns mature into adult-like sleep cycles, often causing the famous 4-month regression.",
                MotorSkills: "• Rolls from tummy to back, and starts attempting back to tummy\n• Reaches out accurately and grasps toys with both hands\n• Pushes up on extended arms during tummy time ('mini push-ups')\n• Strong head control with zero head lag when pulled to sit",
                CognitiveSensory: "• Reaches for objects within visual reach and brings everything to mouth\n• Laughs out loud at silly sounds and surprises\n• Recognizes bottles or breasts when hungry\n• Watches hands and feet move in space (body mapping)",
                SocialSpeech: "• Loud belly laughs, squeals, and blowing raspberries\n• Responds to changes in tone of voice\n• Expresses clear joy when loved ones enter the room",
                RhythmSleep: "• Total sleep: 14–15 hours; wake windows: 90–120 minutes\n• Typical nap schedule: 3–4 naps per day\n• The 4-month sleep regression often causes temporary frequent night wakings",
                PediatricTip: "If baby shows signs of rolling, transition out of swaddles into a safe sleep sack to keep arms free."
            );
        }

        if (daysOld < 240)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 5–8 Months · Sitting, Solids & Teething",
                Summary: "Major leaps in independence: sitting without support, exploring first tastes of solid foods, and early babbling with consonant sounds.",
                MotorSkills: "• Sits independently without hands supporting for several minutes\n• Rolls fluidly both directions (tummy to back, back to tummy)\n• Passes a toy smoothly from one hand to the other\n• Rakes small items with fingers toward palm; bears full weight on legs",
                CognitiveSensory: "• Searches for dropped toys (beginning of object permanence)\n• Explores textures by banging toys together and chewing on teething rings\n• Responds to own name by turning head promptly\n• Shows distinct preferences for favorite toys and foods",
                SocialSpeech: "• Babbles consonant-vowel strings: 'ba-ba', 'ma-ma', 'da-da'\n• Uses sound to express pleasure, impatience, and curiosity\n• Understands emotional tone and stops briefly when hearing 'no'",
                RhythmSleep: "• Total sleep: 13–15 hours; wake windows: 2–2.5 hours\n• Transition from 3 naps down to 2 solid naps (morning and afternoon)\n• Night sleep can reach 8–10 hours with 0–1 night feeds",
                PediatricTip: "Introduce iron-rich single-ingredient foods when baby sits well with good head control and shows strong interest."
            );
        }

        if (daysOld < 365)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 8–12 Months · Crawling, Words & Mobility",
                Summary: "Curiosity takes off on all fours! Babies pull up to stand, develop the fine pincer grasp, and begin communicating with gestures and first words.",
                MotorSkills: "• Crawls on hands and knees, scoots, or army-crawls\n• Pulls up to stand on furniture and cruises along edges\n• Masters the neat pincer grasp (picks up tiny puffs with thumb and forefinger)\n• Stands alone momentarily; may take first wobbly steps!",
                CognitiveSensory: "• Fully understands object permanence (finds hidden items under blankets)\n• Copies actions: bangs spoons, stirs cups, waves goodbye\n• Explores spatial relationships: drops items into bowls and dumps them out",
                SocialSpeech: "• First meaningful words appear ('mama', 'dada', 'ball', 'milk')\n• Waves 'bye-bye', shakes head for 'no', and points with index finger\n• Separation anxiety and stranger awareness peak around 9–10 months",
                RhythmSleep: "• Total sleep: 12–14 hours; wake windows: 2.5–3.5 hours\n• Solid 2-nap routine (morning nap ~1h, afternoon nap ~1.5h)\n• Most babies can sleep 10–11 hours overnight without nighttime feeds",
                PediatricTip: "Baby-proof early! Secure heavy furniture and TVs to walls, cover low electrical outlets, and gate stairways."
            );
        }

        if (daysOld < 545)
        {
            return new DevelopmentStageInfo(
                StageBadge: "✨ 12–18 Months · Walking & First Words",
                Summary: "Transitioning into toddlerhood! Independent walking, finger food mastery, increasing vocabulary, and expressing opinions and independence.",
                MotorSkills: "• Walks independently and starts attempting a fast toddler run\n• Bends down to pick up a toy from the floor without falling over\n• Stacks 2–3 blocks and scribbles with thick crayons\n• Drinks from an open cup and attempts eating with a small spoon",
                CognitiveSensory: "• Points to objects in picture books when asked ('Where is the dog?')\n• Follows simple 1-step directions ('Give me the ball')\n• Imitates everyday chores: sweeps, wipes surfaces, talks on toy phone",
                SocialSpeech: "• Uses 5–20 recognizable words consistently\n• Shakes head firmly and says 'no!' with enthusiasm\n• Shows empathy: hugs a crying sibling or cuddles a stuffed animal",
                RhythmSleep: "• Total sleep: 12–14 hours; wake windows: 4–5 hours\n• Drop from 2 naps down to 1 midday nap (usually around 14–16 months)\n• 12-month and 15-month sleep regressions may cause temporary bedtime resistance",
                PediatricTip: "Offer whole milk or fortified calcium alternatives in cups rather than bottles to protect developing teeth."
            );
        }

        return new DevelopmentStageInfo(
            StageBadge: "✨ 18–24+ Months · Language & Play Explosion",
            Summary: "Active, chatty, and full of personality! Toddlers explore big emotions, combine words into short phrases, and love pretend play.",
            MotorSkills: "• Runs smoothly, kicks balls forward, and climbs stairs holding rails\n• Builds towers of 4–6 blocks; turns door handles and unscrews lids\n• Begins undressing simple clothes and putting on slip-on shoes",
            CognitiveSensory: "• Engages in imaginative pretend play (feeding dolls, driving cars)\n• Sorts toys by color, shape, or size\n• Understands concepts like 'big vs little' and 'in vs out'",
            SocialSpeech: "• 2-word combinations ('more milk', 'big truck', 'mama come')\n• Vocabulary reaches 50+ words with rapid weekly expansion\n• Big emotions and tantrums as desire for autonomy meets limited communication",
            RhythmSleep: "• Total sleep: 11–13 hours daily\n• 1 solid midday nap of 1.5–2.5 hours\n• Consistent calm bedtime routine is key to avoid bedtime battles",
            PediatricTip: "Acknowledge big emotions with calm empathy: 'You're upset that playtime is over. It's okay to feel sad, but it's time for lunch.'"
        );
    }

    public static string[] GetQuestionPool(int daysOld)
    {
        if (daysOld < 14)
        {
            return [
                "How often should a newborn nurse or take a bottle?",
                "Is it normal my newborn sleeps 16-18 hours a day?",
                "What color and consistency should newborn poop be?",
                "How to soothe a crying newborn using the 5 S's?",
                "When should we begin tummy time and for how long?",
                "How do I know if baby is getting enough milk?",
                "How to safely swaddle baby for night sleep?",
                "Best techniques for gentle burping and gas relief?",
                "How to care for the umbilical cord stump?",
                "What room temperature is safest for newborn sleep?",
                "Why is baby breathing fast or pausing when sleeping?",
                "Signs of jaundice: when should I contact the doctor?"
            ];
        }

        if (daysOld < 45)
        {
            return [
                "What are signs of the 3-week and 6-week growth spurts?",
                "Why does baby have a fussy 'witching hour' in the evening?",
                "What are typical awake windows for a 1-month-old?",
                "When do babies start social smiling intentionally?",
                "How to soothe colic, gas pains, and fussiness?",
                "Is frequent spit-up after feeds normal?",
                "What are developmental leaps and Wonder Weeks?",
                "How much tummy time should we do every day?",
                "Safe sleep guidelines: crib setup and pacifiers",
                "How to treat baby acne and cradle cap safely?",
                "Is cluster feeding at night a sign of low milk supply?",
                "Tips for establishing day vs night circadian rhythm"
            ];
        }

        if (daysOld < 90)
        {
            return [
                "When will baby start cooing, laughing, and babbling?",
                "How long can baby stay awake between naps now?",
                "Is 3-4 hours between daytime feeds normal?",
                "When do babies start sleeping longer stretches at night?",
                "How to establish a soothing bedtime routine?",
                "Why is baby constantly sucking and chewing on their hands?",
                "How to help baby track objects and toys across midline?",
                "Recognizing subtle signs of overtiredness vs hunger",
                "Fun developmental play activities for a 2-month-old",
                "How to prevent flat spots on baby's head (plagiocephaly)",
                "How many dirty and wet diapers are expected daily?",
                "When can baby hold their head steady without wobbling?"
            ];
        }

        if (daysOld < 150)
        {
            return [
                "How to navigate the 4-month sleep regression?",
                "When do babies learn to roll from tummy to back?",
                "When and how should we transition out of the swaddle?",
                "How many naps should a 4-month-old take daily?",
                "Signs of early teething vs normal drooling",
                "Can baby reach and grasp toys with both hands accurately?",
                "When can we safely start introducing solid foods?",
                "Best interactive toys and sensory activities for 4 months",
                "Why did night wakings increase suddenly this week?",
                "How to encourage baby to bear weight on legs when standing",
                "Can a 4-month-old drink sips of water?",
                "Transitioning baby from bassinet to crib in nursery"
            ];
        }

        if (daysOld < 240)
        {
            return [
                "Signs baby is ready for solids (purees vs Baby-Led Weaning)?",
                "How to safely introduce peanut butter and allergen foods?",
                "When do babies sit up independently without support?",
                "How much milk should baby drink once starting solids?",
                "Can baby drink small sips of water with solid meals?",
                "How to drop from 4 naps down to 3 (or 2) naps smoothly?",
                "Natural soothing remedies for painful teething gums",
                "Signs baby is preparing to crawl or scoot",
                "How to help baby pass toys from one hand to the other",
                "Managing separation anxiety and stranger wariness",
                "Best iron-rich first foods for 6-month-olds",
                "When will baby start babbling consonant sounds like 'ba-ba'?"
            ];
        }

        if (daysOld < 365)
        {
            return [
                "When do babies learn to crawl, scoot, or army crawl?",
                "Signs of object permanence and understanding peek-a-boo",
                "When will baby say their first intentional words?",
                "Transitioning to a 2-nap schedule (morning and afternoon)",
                "Healthy iron-rich finger foods for self-feeding",
                "Pulling up to stand on furniture and cruising safely",
                "Developing the pincer grasp with soft finger foods",
                "Why is baby waking up in the night to practice standing?",
                "How to baby-proof the house for a newly mobile explorer",
                "Transitioning off bottles to open cups and straw cups",
                "How to handle bedtime separation protests and clinginess",
                "Responding to their name and understanding simple words"
            ];
        }

        if (daysOld < 545)
        {
            return [
                "When will toddler take their first independent steps?",
                "Switching from formula or breastmilk to whole cow's milk",
                "Transitioning from 2 naps down to 1 afternoon nap",
                "How many words should a 15-month-old say?",
                "Managing toddler food throwing and selective eating",
                "How to handle tantrums and frustration calmly?",
                "Signs of readiness to wean off the pacifier",
                "Encouraging speech imitation and animal sounds",
                "Best shoes and foot health for early walkers",
                "Toddler sleep regression at 12 or 15 months: causes & tips",
                "Setting gentle, consistent boundaries with love",
                "Fun sensory and block stacking games for 1-year-olds"
            ];
        }

        return [
            "Language explosion: combining 2-3 words into phrases",
            "Signs of readiness for potty training and when to start",
            "Transitioning from crib to toddler bed safely",
            "Handling big emotions, meltdowns, and tantrums with empathy",
            "Independent eating with utensils and dealing with picky eating",
            "Climbing, running, and physical safety boundaries",
            "Social play: teaching sharing and turn-taking with other kids",
            "Nighttime fears, nightmares, and peaceful bedtime habits",
            "Healthy, nutrient-packed toddler snack ideas",
            "Managing separation anxiety at daycare drop-off",
            "Encouraging imaginative pretend play and creativity",
            "When should I be concerned about speech development delays?"
        ];
    }

    /// <summary>Logged days needed in each week before this week is compared with the one before.</summary>
    public const int DaysToCompare = 4;

    /// <summary>
    /// Questions from her own log (owner, 2026-11-06: questions should fit this baby, not only her age): what changed
    /// between this week and the one before, in the phone's language, at most two, the biggest change first.
    /// </summary>
    public static IReadOnlyList<string> PersonalQuestions(
        string name, int correctedDays, IReadOnlyList<Coo.Mobile.Application.CareLog.DayCare> thisWeek, IReadOnlyList<Coo.Mobile.Application.CareLog.DayCare> lastWeek)
    {
        ArgumentNullException.ThrowIfNull(thisWeek);
        ArgumentNullException.ThrowIfNull(lastWeek);
        var now = thisWeek.Where(d => d.HasEntries).ToList();
        var before = lastWeek.Where(d => d.HasEntries).ToList();
        if (now.Count < DaysToCompare)
            return [];

        var found = new List<(double Size, string Text)>();
        if (before.Count >= DaysToCompare)
        {
            var sleep = now.Average(d => d.Sleep.TotalMinutes) - before.Average(d => d.Sleep.TotalMinutes);
            if (sleep <= -60)
                found.Add((-sleep / 60, Localization.Strings.Format("Advisor.Ask.SleepLess", name)));
            var longest = now.Average(d => d.LongestSleep.TotalMinutes) - before.Average(d => d.LongestSleep.TotalMinutes);
            if (longest <= -60)
                found.Add((-longest / 60, Localization.Strings.Format("Advisor.Ask.LongestShorter", name)));
            else if (longest >= 60)
                found.Add((longest / 90, Localization.Strings.Format("Advisor.Ask.LongestLonger", name)));
            var feeds = now.Average(d => d.Feeds) - before.Average(d => d.Feeds);
            if (feeds >= 2)
                found.Add((feeds / 2, Localization.Strings.Format("Advisor.Ask.FeedsUp", name)));
        }

        // Fewer than five wet diapers a day under six months is worth asking about, whatever last week was.
        if (correctedDays is >= 5 and < 183 && now.All(d => d.Diapers > 0) && now.Average(d => d.Wet) < 5)
            found.Add((2, Localization.Strings.Format("Advisor.Ask.FewWet", name)));

        return [.. found.OrderByDescending(f => f.Size).Take(2).Select(f => f.Text)];
    }

    public static string[] GetFallbackQuestions() => [
        "How much sleep does my baby need at their current age?",
        "Is 3-4 hours between daytime feeds normal?",
        "What are typical wake windows and nap schedules?",
        "Signs of developmental leaps and growth spurts?",
        "How to soothe colic, fussiness, and gas discomfort?",
        "Starting solid foods: signs of readiness and safety tips",
        "When will baby start rolling over and sitting up?",
        "Safe sleep habits: crib setup, temperature, and swaddling"
    ];
}
