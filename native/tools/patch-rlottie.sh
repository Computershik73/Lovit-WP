#!/bin/sh
# Приводит исходники rlottie к тому, что понимает компилятор VS 2013.
#
#     sh native/tools/patch-rlottie.sh
#
# Зачем. Остальные наши нативные части собраны VS 2013, и иначе нельзя:
# Windows Phone 8.1 живёт на среде выполнения msvcr120_app, а среды от
# VS 2015 (VCLibs 14.0) под эту систему Microsoft не выпускала вовсе —
# собранное новым компилятором на телефоне просто не запустится.
#
# Сама rlottie на C++14 не написана: обобщённых лямбд в ней нет, а
# make_unique в VS 2013 уже есть. Мешают три вещи из C++11, которых тот
# компилятор не знает: constexpr, noexcept и alignas. Все три убираются
# без изменения смысла — constexpr становится обычной подстановкой или
# постоянной, noexcept пропадает (это лишь обещание не бросать
# исключений), alignas заменяется своим объявлением Microsoft.
#
# Сценарий рассчитан на повторный запуск: он ничего не ломает, если
# правки уже наложены.

set -e

ROOT="$(dirname "$0")/../../vendor/rlottie"

if [ ! -f "$ROOT/src/lottie/lottieanimation.cpp" ]; then
  echo "НЕТ rlottie: ожидался $ROOT/src"
  exit 2
fi

FILES=$(find "$ROOT/src" "$ROOT/inc" -name '*.cpp' -o -name '*.h')

for f in $FILES; do
  # 1. Отдельные места, где общее правило не годится.
  sed -i 's/static auto constexpr size = N;/static const std::size_t size = N;/' "$f"
  sed -i 's/static constexpr inline /static inline /' "$f"
  sed -i 's/constexpr const /const /g' "$f"

  # 2. Постоянные: со знаком присваивания и без скобок до него — это
  #    данные, а не функция, и им подходит const.
  sed -i -E 's/(^|[[:space:]])constexpr ([A-Za-z_][A-Za-z_0-9:<> ]*[A-Za-z_0-9]+[[:space:]]*(\[[0-9]*\])?[[:space:]]*=)/\1const \2/g' "$f"

  # 3. Всё оставшееся — на функциях и способах построения: там constexpr
  #    означает лишь «можно посчитать заранее», и обычная подстановка
  #    ведёт себя так же, только считает во время работы.
  sed -i 's/\bconstexpr\b/inline/g' "$f"

  # 3a. «constexpr inline» после замены даёт «inline inline» — такое
  #     повторение компилятор не принимает.
  sed -i 's/\binline inline\b/inline/g' "$f"

  # 4. Обещание не бросать исключений. VS 2013 его не понимает, а на
  #    смысл оно не влияет: throw() значит то же самое.
  sed -i -E 's/\bnoexcept\b//g' "$f"

  # 4a. Рядом с `= default` стоит способ построения с переменным числом
  #     доводов, и при нуле доводов он тоже даёт построение по умолчанию.
  #     VS 2013 считает это двумя разными умолчаниями и отказывается;
  #     пишем построение явно — смысл тот же.
  sed -i 's/^\([[:space:]]*\)model() = default;$/\1model() : mValue() {}/' "$f"

  # 4b. Запрос выравнивания. Подменить его определением нельзя —
  #     стандартная библиотека нарочно ловит подмену ключевых слов и
  #     останавливает сборку, — поэтому правим сам текст.
  sed -i -E 's/\balignof\(/__alignof(/g' "$f"

  # 5. Выравнивание — своим объявлением Microsoft.
  sed -i -E 's/struct alignas\(([0-9]+)\) /struct __declspec\(align\(\1\)\) /' "$f"
  sed -i -E 's/alignas\(alignment\) char/__declspec(align(16)) char/' "$f"
done

# 6. Объединения с непростыми членами — это тоже C++11, и VS 2013 их не
#    принимает. Заменяем обычными записями: там, где раньше поля делили
#    одну память, теперь у каждого своя. Расход больше на несколько
#    десятков байтов на кисть, а ведёт себя всё так же — эти поля и
#    прежде выбирались по отдельному признаку вида, а не по совпадению
#    памяти.
sed -i 's/^    union {$/    struct {/' "$ROOT/src/vector/vbrush.h"

# 6a. И сразу следствие, без которого замена опасна.
#
#     В объединении поля лежали друг на друге, и обнуление цвета —
#     mColor{} — заодно обнуляло первые четыре байта указателя, то есть
#     сам указатель. В обычной записи у каждого поля своя память, и
#     указатели остались бы с мусором: проверка «а есть ли градиент»
#     прочла бы случайное число и ушла по нему.
sed -i 's/^        const VGradient \*mGradient;$/        const VGradient *mGradient{nullptr};/' "$ROOT/src/vector/vbrush.h"
sed -i 's/^        const VTexture  \*mTexture;$/        const VTexture  *mTexture{nullptr};/' "$ROOT/src/vector/vbrush.h"

# 7. То же в свойствах разбираемой сцены, но там объединение вдобавок
#    заводило и разрушало свои поля вручную — размещающим new и явным
#    вызовом разрушителя. Раз поля теперь живут каждое своей жизнью,
#    заведение становится обычным присваиванием, а разрушать вручную
#    больше нечего: поля разрушатся сами.
MODEL="$ROOT/src/lottie/lottiemodel.h"
sed -i 's/^    union details {$/    struct details {/' "$MODEL"
sed -i 's/^        new (&member) Tp(std::move(val));$/        member = std::move(val);/' "$MODEL"
sed -i '/impl_\.value_\.~T();/d' "$MODEL"
sed -i '/impl_\.animation_\.~unique_ptr<Animation>();/d' "$MODEL"

# 8. Такое же умолчание, как у vcow_ptr, но под другим именем.
sed -i 's/^    vshared_ptr() = default;$/    vshared_ptr() {}/' "$ROOT/src/vector/vsharedptr.h"

# 9. Превращение сцены хранит либо указатель на изменяемые данные, либо
#    неизменную заготовку. Заготовке теперь нужно построение по
#    умолчанию: раньше её заводили вручную, а в обычной записи она
#    появляется вместе со всем остальным.
if ! grep -q "StaticData() : mOpacity(0)" "$MODEL"; then
  sed -i 's|^        StaticData(VMatrix \&\&m, float opacity)$|        StaticData() : mOpacity(0) {}\n        StaticData(VMatrix \&\&m, float opacity)|' "$MODEL"
fi

# 10. Ещё одно такое же объединение — в подменах свойств, где хранятся
#     переданные наружу способы вычисления. Приём тот же.
FILTER="$ROOT/src/lottie/lottiefiltermodel.h"
sed -i 's/^    union details {$/    struct details {/' "$FILTER"
sed -i '/impl\.[a-zA-Z]*Func\.~[A-Za-z]*Func();/d' "$FILTER"

# 11. Подмена свойств сцены на ходу (перекрасить слой, сдвинуть его) —
#     возможность открытая, но нам не нужная: мы только открываем стикер
#     и просим кадры. VS 2013 разбирает её вызовы неоднозначно, а чинить
#     то, чем никто не пользуется, незачем — убираем целиком. Объявления
#     в заголовке остаются, но звать их некому.
sed -i '/^void Animation::setValue(/,/^}$/d' "$ROOT/src/lottie/lottieanimation.cpp"

# 12. Единственная на всю библиотеку обобщённая лямбда — вот её VS 2013
#     действительно не умеет. Тип довода выводим из самого набора: так
#     не нужно называть его вслух и правка не зависит от внутренностей.
sed -i 's/\[id\](const auto \&val)/[id](const decltype(mLayers)::value_type \&val)/' "$ROOT/src/lottie/lottieitem.cpp"

# 13. Заведение пустого значения теперь лишнее: поле и так появляется
#     вместе с записью. Вдобавок VS 2013 не выводит тип из пустых
#     фигурных скобок.
sed -i 's/^    Property() { construct(impl_.value_, {}); }$/    Property() {}/' "$MODEL"

# 14. Запись, объявленная внутри способа с переменным типом и задающая
#     начальные значения прямо при полях, сбивает VS 2013 с толку: после
#     неё он перестаёт видеть в теле обычные обращения к своим же полям.
#     Задаём начальные значения построением — смысл тот же.
perl -0pi -e 's/    struct ParsedField \{\n        std::string interpolatorKey;\n        bool        interpolator\{false\};\n        bool        value\{false\};\n        bool        hold\{false\};\n        bool        noEndValue\{true\};\n    \};/    struct ParsedField {\n        std::string interpolatorKey;\n        bool        interpolator;\n        bool        value;\n        bool        hold;\n        bool        noEndValue;\n        ParsedField() : interpolator(false), value(false), hold(false),\n                        noEndValue(true) {}\n    };/' "$ROOT/src/lottie/lottieparser.cpp"

echo "rlottie приведена к VS 2013"
