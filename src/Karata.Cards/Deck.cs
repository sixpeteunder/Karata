using System;
using System.Collections.Generic;
using Karata.Cards.Shufflers;
using static Karata.Cards.Card;
using static Karata.Cards.Card.CardFace;
using static Karata.Cards.Card.CardSuit;
using static Karata.Cards.Shufflers.ShuffleAlgorithm;

namespace Karata.Cards
{
    public class Deck
    {
        public Stack<Card> Cards { get; set; } = new();

        public static Deck StandardDeck
        {
            get
            {
                var cards = new Stack<Card>();
                foreach (var suit in Enum.GetValues<CardSuit>())
                {
                    if (suit is BlackJoker or RedJoker) continue;
                    foreach (var face in Enum.GetValues<CardFace>())
                    {
                        if (face is None) continue;
                        cards.Push(new(suit, face));
                    }
                }

                cards.Push(new(BlackJoker, None));
                cards.Push(new(RedJoker, None));
                return new() { Cards = new(cards) };
            }
        }

        // Use a custom shuffling function
        public void Shuffle(Func<Stack<Card>, Stack<Card>> shuffleFunc) => Cards = shuffleFunc(Cards);

        // Use a custom IShuffler
        public void Shuffle(IShuffler shuffler) => Shuffle(shuffleFunc: shuffler.Shuffle);

        // Use a built-in shuffle algorithm.
        // Use (and fall back on) Fisher-Yates shuffle by default.
        // TODO: Source Generators for built-in shufflers.
        public void Shuffle(ShuffleAlgorithm shuffleAlgorithm = Default) =>
            Shuffle(shuffler: shuffleAlgorithm switch
            {
                OrderByRandom => new OrderByRandomShuffler(),
                FisherYates or _ => new FisherYatesShuffler()
            });

        public Card Deal() => Cards.Pop();

        // TODO: Check for cards == 0
        public List<Card> DealMany(int num)
        {
            var list = new List<Card>();
            for(int i = 0; i < num; i++)
            {
                list.Add(Cards.Pop());
            }
            return list;
        }
    }
}