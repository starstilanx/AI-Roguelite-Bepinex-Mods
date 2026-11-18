AIROG Schedules 1.0.0 - README
==============================

WHAT IT DOES
------------
Schedules lets you tell the storyteller what normally happens where, and when.

  - Timetables for places: "From 15:30 to 17:00 on Monday and Wednesday the
    debate club meets in the Debate Club Room."
  - Routines for characters: "Ms. Tanaka is in the Staff Room from 12:00 to
    13:00 on school days."

Every time the game builds a prompt, the mod looks at the in-game weekday, the
time of day and where you are standing, and adds a short note for the AI:

  - what is happening HERE right now,
  - what starts here soon or has just ended (so a bell can ring, or people can
    be packing up),
  - who is EXPECTED here right now,
  - which characters in the scene (or named in the story text) would normally
    be SOMEWHERE ELSE at this hour,
  - what is going on in the other rooms of the location you are in.

When nothing in your schedule applies, nothing is added.

The schedule is a strong default, not a law. The AI is told that people usually
keep to it and that it may break it when the story gives a reason. It is also
told never to read the schedule out to you.

The game's own calendar is for one-off and simple repeating events (daily or
weekly). Schedules is for the weekly rhythm of a place: several days of the
week, a time window, a location, and optionally the people involved.


HOW TO USE IT
-------------
Press the "Sched" button on the HUD (next to the other mod buttons).

The top of the panel shows the weekday and time the mod is working with and
where it thinks you are. If that line is right, your schedule will be right.

  Add entry   opens a form. The place is filled in with where you are standing.
  Edit        the same form for an existing entry.
  Duplicate   copies an entry (handy for a timetable: copy, change the time).
  Delete      asks once more, then removes it.
  On / Off    parks an entry without deleting it.

Entries that are running right now are shown in green and marked ON NOW.

The form:

  Title        Short. "Debate club", "Lunch break", "Night shift at the forge".
  Details      Optional. One or two sentences the AI should know while it is on.
  Place        The name of a sub-location, a location or a region, as the game
               shows it. Capitals and extra spaces do not matter, and a leading
               "the" is ignored. An entry on a location also applies in every
               room inside it; an entry on a region applies everywhere in that
               region. Leave it blank for "anywhere".
  Start, End   Time of day. All of these work: 8   8:30   0830   8pm   20:15
               If the end is earlier than the start, the entry runs past
               midnight. If the end equals the start, it runs for a full day.
  Characters   Names, comma separated. Optional for an event, needed for a
               routine. "Tanaka" also matches a character called "Ms. Tanaka".
  Kind         Event   = something happening at the place.
               Routine = where the listed characters normally are.
  Days         Tick the days it runs on. All days ticked means every day.

If something in the form cannot be understood (a time like 25:99, a routine
without a character, no day ticked) the form stays open, keeps what you typed
and tells you what is wrong.


A WORKED EXAMPLE: A SCHOOL
--------------------------
The school is a location called "Seika Academy" with rooms inside it
(Classroom 2-B, Cafeteria, Staff Room, Debate Club Room).

  Title: Morning classes
  Place: Seika Academy          Days: Monday to Friday     08:30 - 12:00
  Kind:  Event
  Details: Lessons are in session. Corridors are empty, students found outside
           class are asked for a hall pass.

  Title: Lunch break
  Place: Seika Academy          Days: Monday to Friday     12:00 - 13:00
  Kind:  Event
  Details: Students crowd the cafeteria and the courtyard.

  Title: Afternoon classes
  Place: Seika Academy          Days: Monday to Friday     13:00 - 15:30
  Kind:  Event

  Title: Debate club
  Place: Debate Club Room       Days: Monday, Wednesday    15:30 - 17:00
  Kind:  Event                  Characters: Aiko, Ren
  Details: Members argue a set motion. Visitors may watch from the back.

  Title: Teaches class 2-B
  Place: Classroom 2-B          Days: Monday to Friday     08:30 - 12:00
  Kind:  Routine                Characters: Ms. Tanaka

  Title: Lunch in the staff room
  Place: Staff Room             Days: Monday to Friday     12:00 - 13:00
  Kind:  Routine                Characters: Ms. Tanaka

  Title: Night watch
  Place: Seika Academy          Days: every day            22:00 - 06:00
  Kind:  Routine                Characters: Mr. Sato
  Details: Walks the corridors with a torch and locks what he finds open.

What the AI then knows:

  - In the Cafeteria on Monday at 12:10: lunch break is on across the academy,
    morning classes ended ten minutes ago, the debate club is not on yet.
  - If you talk to Ms. Tanaka there at that time: she would normally be in the
    Staff Room, so her being in the cafeteria is unusual, or she is on her way.
  - In the Debate Club Room on Wednesday at 16:00: the club is in session and
    Aiko and Ren should be there.
  - In a corridor at 23:30: Mr. Sato is expected somewhere in the academy on
    his night watch. The watch that began on Sunday at 22:00 is still running
    at 01:00 on Monday.


WHICH DAY IS IT?
----------------
The mod uses the game's own clock, the one shown on the HUD.

  - If the world's calendar has day names (most do, including custom calendars
    with a week that is not seven days long), the mod uses exactly those names
    and that week length. It can never disagree with the date on the HUD.

  - If the calendar has a clock but no day names, the mod makes a week itself:
    weekday = (in-game day number + an offset stored in this save) counted
    around the week. Day names come from the config file (Monday to Sunday by
    default) and the week length can be set per save with the - and + buttons
    in the panel (default from the config file).

  - If the save has no in-game calendar at all, it has no time of day.
    Schedules are kept but cannot apply, and the panel says so.

To correct the weekday, use "Today is  <  Wednesday  >" at the top of the
panel. With a game calendar this changes the game calendar's own day-of-week
setting (the same one you can edit by right-clicking the date on the HUD), so
the HUD date changes too. Calendar events keep their date and time.

A day flag belongs to the day an entry STARTS on. "Friday, 23:00 to 02:00" is
running at 00:30 on Saturday, because it began on Friday night. It is not
running at 00:30 on Friday unless Thursday is ticked as well.


SHARING A SETUP
---------------
  Export   writes schedules_export.json into the folder of the current save.
  Import   reads schedules_export.json from that folder and adds its entries.
           Entries you already have are skipped.
  Folder   opens that folder.

The file is plain text and can be edited by hand. Days are written as weekday
names of that world, or "every day". If a shared file uses day names your
world does not have, the affected entries are flagged in the list; open them
with Edit and tick the right days.

A world preset folder that contains a schedules.json starts new games with
those schedules.


SWITCHING IT OFF
----------------
  For one save:   the "Mods" button on the HUD, untick "Schedules". The HUD
                  button disappears and nothing is added to prompts. Your
                  entries stay in the save and come back when you tick it.
  For all saves:  Enabled = false in BepInEx/config/com.airog.schedules.cfg


LIMITS OF VERSION 1
-------------------
  - The mod never moves, creates or removes a character. It tells the
    storyteller where people should be; the storyteller, which can already
    bring characters into a scene and send them away, does the rest. A
    character who is "expected here" appears when the AI writes them in, not
    the instant you open the door.
  - The AI can still ignore a schedule. It is told the schedule is the normal
    state of things, not that it is forbidden to deviate.
  - Places and characters are matched by NAME. If a place or a character is
    renamed in the game, edit the entry. A character matches when the names
    are equal or one is a whole-word part of the other.
  - The schedule repeats weekly. For a one-off event, use the game's calendar.
  - Entries are written by you. The AI does not create or change them.
  - Needs the in-game calendar and clock (see "Which day is it?").
  - In co-op, the host's schedules are the ones that apply.


CONSOLE COMMANDS
----------------
  SCHEDULE_NOW    the weekday and time the mod computes, the chain of places
                  you are in, who is present, and exactly the text that would
                  be added to a prompt right now.
  SCHEDULE_LIST   every entry, with its state.


CONFIG (BepInEx/config/com.airog.schedules.cfg)
-----------------------------------------------
  [General]
  Enabled                 true    Master switch for all saves.

  [Calendar]              Only for calendars that have no day names.
  WeekdayNames            Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday
  DefaultWeekLength       7

  [Injection]
  SoonWindowMinutes       30      "Starting soon" / "just ended" window, in
                                  in-game minutes. 0 turns both off.
  MaxHappeningNow         4       Most lines per section.
  MaxSoonOrEnded          3
  MaxExpectedHere         5
  MaxNormallyElsewhere    4
  MaxElsewhereInLocation  3       0 turns the "other rooms" section off.
  MaxDetailChars          160     Longer details are cut at a word.
  MaxBlockChars           1600    Ceiling for the whole note (about 400 tokens).

An optional HUD icon can be placed at
  AI Roguelite_Data/StreamingAssets/Schedules/SchedulesButton.png
